param(
    [ValidateSet("Install", "Upgrade", "Uninstall")]
    [string]$Action = "Install"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$releaseBundlePath = Join-Path $root "RotationTracker.msixbundle"
$releaseCertPath = Join-Path $root "RotationTracker.cer"
$packageNamePattern = "RotationTracker*"
$scheduledTaskName = "RotationTracker Elevated Backend"
$backupDir = Join-Path $env:LOCALAPPDATA "RotationTracker"
$settingsBackupPath = Join-Path $backupDir "rotation-settings.json"
$elevatedBackendDir = Join-Path $backupDir "ElevatedBackend"
$elevatedBackendExePath = Join-Path $elevatedBackendDir "RotationTracker.Backend.exe"
$elevatedBackendSupervisorPath = Join-Path $backupDir "Start-ElevatedBackend.ps1"
$currentProgressId = 1

function Show-Step {
    param(
        [int]$Percent,
        [string]$Status
    )

    $totalBlocks = 30
    $filledBlocks = [Math]::Min($totalBlocks, [Math]::Max(0, [int][Math]::Round(($Percent / 100.0) * $totalBlocks)))
    $bar = ("#" * $filledBlocks).PadRight($totalBlocks, "-")
    Write-Host ""
    Write-Host "[$Percent%] $Status" -ForegroundColor Cyan
    Write-Host "[$bar]" -ForegroundColor DarkGray
}

function Complete-Steps {
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-InstalledPackage {
    return Get-AppxPackage -Name $packageNamePattern -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Ensure-BackupDirectory {
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
}

function Get-LocalStatePath {
    param([string]$PackageFamilyName)

    if ([string]::IsNullOrWhiteSpace($PackageFamilyName)) {
        return $null
    }

    return Join-Path $env:LOCALAPPDATA ("Packages\{0}\LocalState" -f $PackageFamilyName)
}

function Backup-RotationSettings {
    $package = Get-InstalledPackage
    if (-not $package) {
        return
    }

    $localState = Get-LocalStatePath -PackageFamilyName $package.PackageFamilyName
    $sourcePath = Join-Path $localState "rotation-settings.json"
    if (-not (Test-Path $sourcePath)) {
        return
    }

    Ensure-BackupDirectory
    Copy-Item -LiteralPath $sourcePath -Destination $settingsBackupPath -Force
    Write-Host "Backed up rotations to $settingsBackupPath"
}

function Restore-RotationSettings {
    if (-not (Test-Path $settingsBackupPath)) {
        return
    }

    $package = Get-InstalledPackage
    if (-not $package) {
        return
    }

    $localState = Get-LocalStatePath -PackageFamilyName $package.PackageFamilyName
    if ([string]::IsNullOrWhiteSpace($localState)) {
        return
    }

    New-Item -ItemType Directory -Path $localState -Force | Out-Null
    Copy-Item -LiteralPath $settingsBackupPath -Destination (Join-Path $localState "rotation-settings.json") -Force
    Write-Host "Restored rotations from $settingsBackupPath"
}

function Stop-AppProcesses {
    $processes = @(
        "RotationTracker",
        "RotationTracker.Backend",
        "GameBar",
        "GameBarFT",
        "GameBarFTServer",
        "GameBarElevatedFT_Player",
        "gamebarpresencewriter",
        "GamePanel"
    )

    Get-Process -Name $processes -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    try {
        & taskkill /IM "RotationTracker.Backend.exe" /F 1>$null 2>$null
    }
    catch {
    }
}

function Trust-Certificate {
    param([string]$CertificatePath)

    if (-not (Test-Path $CertificatePath)) {
        throw "Certificate file not found: $CertificatePath"
    }

    Import-Certificate -FilePath $CertificatePath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
}

function Get-NativeArchitectureFolderName {
    $architecture = $env:PROCESSOR_ARCHITECTURE
    if (-not $architecture) {
        $architecture = ""
    }

    switch ($architecture.ToUpperInvariant()) {
        "AMD64" { return "x64" }
        "X86" { return "x86" }
        "ARM64" { return "arm64" }
        default { return "x64" }
    }
}

function Get-ReleaseDependencyPaths {
    $paths = Get-ChildItem -Path (Join-Path $root "*") -File -Include "*.appx", "*.msix" |
        Where-Object { $_.FullName -ne $releaseBundlePath } |
        Select-Object -ExpandProperty FullName

    return Get-UniqueDependencyPaths -Paths $paths
}

function Get-UniqueDependencyPaths {
    param([string[]]$Paths)

    $seen = @{}
    $unique = @()
    foreach ($path in (($Paths | Where-Object { $_ }) | Sort-Object -Unique)) {
        $name = [System.IO.Path]::GetFileName($path).ToLowerInvariant()
        if ($seen.ContainsKey($name)) {
            continue
        }

        $seen[$name] = $true
        $unique += $path
    }

    return $unique
}

function Remove-InstalledPackage {
    $packages = @(Get-AppxPackage -Name $packageNamePattern -ErrorAction SilentlyContinue)
    foreach ($package in $packages) {
        Remove-AppxPackage -Package $package.PackageFullName -ErrorAction SilentlyContinue | Out-Null
    }
}

function Resolve-InstalledBackendInfo {
    $package = Get-InstalledPackage
    if (-not $package) {
        throw "RotationTracker package is not installed."
    }

    $backendPath = Join-Path $package.InstallLocation "RotationTracker.Backend\RotationTracker.Backend.exe"
    if (-not (Test-Path $backendPath)) {
        throw "Backend executable not found: $backendPath"
    }

    $typeDef = @"
using System;
using System.Runtime.InteropServices;

public static class AppContainerSidHelper
{
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string DeriveSidString(string packageFamilyName)
    {
        IntPtr sidPtr = IntPtr.Zero;
        IntPtr sidStringPtr = IntPtr.Zero;
        try
        {
            int hr = DeriveAppContainerSidFromAppContainerName(packageFamilyName, out sidPtr);
            if (hr != 0 || sidPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("DeriveAppContainerSidFromAppContainerName failed: " + hr);
            }

            if (!ConvertSidToStringSid(sidPtr, out sidStringPtr) || sidStringPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("ConvertSidToStringSid failed.");
            }

            return Marshal.PtrToStringUni(sidStringPtr);
        }
        finally
        {
            if (sidPtr != IntPtr.Zero) LocalFree(sidPtr);
            if (sidStringPtr != IntPtr.Zero) LocalFree(sidStringPtr);
        }
    }
}
"@

    if (-not ("AppContainerSidHelper" -as [type])) {
        Add-Type -TypeDefinition $typeDef | Out-Null
    }

    return [pscustomobject]@{
        Package = $package
        BackendPath = $backendPath
        PackageSid = [AppContainerSidHelper]::DeriveSidString($package.PackageFamilyName)
    }
}

function Install-ElevatedBackendFilesFromRelease {
    $sourceDir = Join-Path $root "ElevatedBackend"
    if (-not (Test-Path $sourceDir)) {
        throw "Elevated backend files were not found in the installer package."
    }

    Ensure-BackupDirectory
    New-Item -ItemType Directory -Path $elevatedBackendDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir "*") -Destination $elevatedBackendDir -Recurse -Force
}

function Install-ElevatedBackendSupervisor {
    Ensure-BackupDirectory

    $script = @'
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendPath,

    [Parameter(Mandatory = $true)]
    [string]$PackageSid
)

$ErrorActionPreference = "Stop"
$restartDelaySeconds = 2
$minHealthyRunSeconds = 10

while ($true) {
    if (-not (Test-Path -LiteralPath $BackendPath)) {
        Start-Sleep -Seconds $restartDelaySeconds
        continue
    }

    $startedAt = Get-Date
    $process = Start-Process -FilePath $BackendPath -ArgumentList "`"$PackageSid`"" -PassThru -WindowStyle Hidden
    $process.WaitForExit()

    $runtimeSeconds = ((Get-Date) - $startedAt).TotalSeconds
    if ($runtimeSeconds -lt $minHealthyRunSeconds) {
        Start-Sleep -Seconds $restartDelaySeconds
    }
}
'@

    Set-Content -LiteralPath $elevatedBackendSupervisorPath -Value $script -Encoding UTF8 -Force
}

function Install-FromReleaseAssets {
    Show-Step -Percent 30 -Status "Trusting certificate"
    if (-not (Test-Path $releaseBundlePath)) {
        throw "Release bundle not found: $releaseBundlePath"
    }
    if (-not (Test-Path $releaseCertPath)) {
        throw "Release certificate not found: $releaseCertPath"
    }

    Trust-Certificate -CertificatePath $releaseCertPath

    Show-Step -Percent 45 -Status "Stopping running app processes"
    Stop-AppProcesses

    Show-Step -Percent 55 -Status "Removing previous package"
    Remove-InstalledPackage

    Show-Step -Percent 70 -Status "Installing package files"
    $dependencyPaths = Get-ReleaseDependencyPaths
    if ($dependencyPaths.Count -gt 0) {
        Add-AppxPackage -Path $releaseBundlePath -DependencyPath $dependencyPaths -ForceApplicationShutdown
    }
    else {
        Add-AppxPackage -Path $releaseBundlePath -ForceApplicationShutdown
    }

    Show-Step -Percent 78 -Status "Installing elevated backend helper"
    Install-ElevatedBackendFilesFromRelease
}

function Install-Package {
    if (-not (Test-Path $releaseBundlePath)) {
        throw "Release bundle not found: $releaseBundlePath"
    }

    Install-FromReleaseAssets
}

function Register-ElevatedBackendTask {
    $backend = Resolve-InstalledBackendInfo
    $userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    Install-ElevatedBackendSupervisor
    $action = New-ScheduledTaskAction `
        -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$elevatedBackendSupervisorPath`" -BackendPath `"$elevatedBackendExePath`" -PackageSid `"$($backend.PackageSid)`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable

    Register-ScheduledTask `
        -TaskName $scheduledTaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
}

function Unregister-ElevatedBackendTask {
    Stop-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue | Out-Null
    Unregister-ScheduledTask -TaskName $scheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}

function Remove-ElevatedBackendFiles {
    if (Test-Path -LiteralPath $elevatedBackendSupervisorPath) {
        Remove-Item -LiteralPath $elevatedBackendSupervisorPath -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $elevatedBackendDir) {
        Remove-Item -LiteralPath $elevatedBackendDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Start-ElevatedBackend {
    $task = Get-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        throw "Scheduled task '$scheduledTaskName' was not found."
    }

    Start-ScheduledTask -TaskName $scheduledTaskName | Out-Null
    Start-Sleep -Milliseconds 1500

    $backendRunning = @(Get-Process -Name "RotationTracker.Backend" -ErrorAction SilentlyContinue).Count -gt 0
    if ($backendRunning) {
        Write-Host "Elevated backend task started successfully." -ForegroundColor Green
        return
    }

    $taskInfo = Get-ScheduledTaskInfo -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    if ($taskInfo) {
        if ($taskInfo.LastTaskResult -eq 267009) {
            Write-Host "Scheduled task is still running. The elevated backend may still be starting." -ForegroundColor Yellow
            return
        }

        Write-Host "Scheduled task ran, but the backend process was not detected. LastTaskResult=$($taskInfo.LastTaskResult)" -ForegroundColor Yellow
        return
    }

    Write-Host "Scheduled task was started, but its status could not be verified." -ForegroundColor Yellow
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated shell."
}

try {
    switch ($Action) {
        "Install" {
            Show-Step -Percent 10 -Status "Backing up saved rotations"
            Backup-RotationSettings

            Show-Step -Percent 20 -Status "Preparing install"
            Install-Package

            Show-Step -Percent 82 -Status "Restoring saved rotations"
            Restore-RotationSettings

            Show-Step -Percent 90 -Status "Registering elevated startup task"
            Register-ElevatedBackendTask

            Show-Step -Percent 96 -Status "Starting elevated backend"
            Start-ElevatedBackend
            Complete-Steps
            Write-Host ""
            Write-Host "Install complete."
            Write-Host "Rotations backup: $settingsBackupPath"
            break
        }

        "Upgrade" {
            Show-Step -Percent 10 -Status "Backing up saved rotations"
            Backup-RotationSettings

            Show-Step -Percent 20 -Status "Preparing upgrade"
            Install-Package

            Show-Step -Percent 82 -Status "Restoring saved rotations"
            Restore-RotationSettings

            Show-Step -Percent 90 -Status "Refreshing elevated startup task"
            Register-ElevatedBackendTask

            Show-Step -Percent 96 -Status "Starting elevated backend"
            Start-ElevatedBackend
            Complete-Steps
            Write-Host ""
            Write-Host "Upgrade complete."
            Write-Host "Rotations backup: $settingsBackupPath"
            break
        }

        "Uninstall" {
            Show-Step -Percent 15 -Status "Backing up saved rotations"
            Backup-RotationSettings

            Show-Step -Percent 35 -Status "Stopping running app processes"
            Stop-AppProcesses

            Show-Step -Percent 60 -Status "Removing elevated startup task"
            Unregister-ElevatedBackendTask

            Show-Step -Percent 70 -Status "Removing elevated backend helper"
            Remove-ElevatedBackendFiles

            Show-Step -Percent 80 -Status "Removing package"
            Remove-InstalledPackage
            Complete-Steps
            Write-Host ""
            Write-Host "Uninstall complete."
            Write-Host "Rotations were preserved at $settingsBackupPath"
            break
        }
    }
}
catch {
    Complete-Steps
    Write-Host ""
    Write-Host "Operation failed." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    Complete-Steps
}
