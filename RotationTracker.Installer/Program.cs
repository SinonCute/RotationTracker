using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace RotationTracker.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("--supervise-backend", StringComparison.OrdinalIgnoreCase))
        {
            return BackendSupervisor.Run(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm(ResolveAction(args)));
        return 0;
    }

    private static InstallerAction ResolveAction(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--action", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAction(args[1]);
        }

        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        return exeName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            ? InstallerAction.Uninstall
            : InstallerAction.Install;
    }

    private static InstallerAction ParseAction(string action)
    {
        if (action.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerAction.Install;
        }

        if (action.Equals("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerAction.Uninstall;
        }

        throw new ArgumentException("Only install and uninstall actions are supported.");
    }
}

internal enum InstallerAction
{
    Install,
    Uninstall
}

internal sealed class InstallerForm : Form
{
    private readonly InstallerAction _action;
    private readonly InstallerEngine _engine;
    private readonly TextBox _logBox = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Button _closeButton = new();
    private readonly Label _statusLabel = new();

    public InstallerForm(InstallerAction action)
    {
        _action = action;
        _engine = new InstallerEngine(AppendLog);
        InitializeUi();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RunInstallerAsync();
    }

    private void InitializeUi()
    {
        Text = _action == InstallerAction.Uninstall ? "Rotation Tracker Uninstall" : "Rotation Tracker Install";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 420);
        MinimumSize = new Size(560, 320);
        MaximizeBox = false;

        _statusLabel.AutoSize = false;
        _statusLabel.Text = _action == InstallerAction.Uninstall ? "Uninstalling Rotation Tracker..." : "Installing Rotation Tracker...";
        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 44;
        _statusLabel.Padding = new Padding(12, 12, 12, 0);

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 16;
        _progressBar.Style = ProgressBarStyle.Marquee;

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 9F);
        _logBox.BackColor = Color.White;

        _closeButton.Text = "Close";
        _closeButton.Enabled = false;
        _closeButton.Width = 92;
        _closeButton.Height = 32;
        _closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _closeButton.Click += (_, _) => Close();

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12)
        };
        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Resize += (_, _) =>
        {
            _closeButton.Left = buttonPanel.ClientSize.Width - _closeButton.Width - 12;
            _closeButton.Top = 10;
        };

        Controls.Add(_logBox);
        Controls.Add(_progressBar);
        Controls.Add(_statusLabel);
        Controls.Add(buttonPanel);
    }

    private async Task RunInstallerAsync()
    {
        try
        {
            if (!InstallerEngine.IsAdministrator())
            {
                throw new InvalidOperationException("Installer must run as administrator.");
            }

            if (_action == InstallerAction.Uninstall)
            {
                await _engine.UninstallAsync();
                _statusLabel.Text = "Uninstall complete.";
            }
            else
            {
                await _engine.InstallAsync();
                _statusLabel.Text = "Install complete.";
            }
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            _statusLabel.Text = _action == InstallerAction.Uninstall ? "Uninstall failed." : "Install failed.";
        }
        finally
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 100;
            _closeButton.Enabled = true;
        }
    }

    private void AppendLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }

        _logBox.AppendText(text + Environment.NewLine);
    }
}

internal sealed class InstallerEngine
{
    private const string PackageName = "RotationTracker";
    private const string ScheduledTaskName = "RotationTracker Elevated Backend";
    private readonly Action<string?> _log;
    private readonly string _root;
    private readonly string _backupDir;
    private readonly string _settingsBackupPath;
    private readonly string _elevatedBackendDir;
    private readonly string _elevatedBackendExePath;
    private readonly string _supervisorExePath;

    public InstallerEngine(Action<string?> log)
    {
        _log = log;
        _root = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RotationTracker");
        _settingsBackupPath = Path.Combine(_backupDir, "rotation-settings.json");
        _elevatedBackendDir = Path.Combine(_backupDir, "ElevatedBackend");
        _elevatedBackendExePath = Path.Combine(_elevatedBackendDir, "RotationTracker.Backend.exe");
        _supervisorExePath = Path.Combine(_elevatedBackendDir, "RotationTracker.Supervisor.exe");
    }

    public async Task InstallAsync()
    {
        Log("Backing up saved rotations");
        BackupRotationSettings();

        Log("Trusting release certificate");
        TrustCertificate();

        Log("Stopping running app processes");
        StopAppProcesses();

        Log("Removing previous package");
        await RemoveInstalledPackagesAsync();

        Log("Installing package files");
        await InstallPackageAsync();

        Log("Installing elevated backend helper");
        InstallElevatedBackendFiles();

        Log("Restoring saved rotations");
        RestoreRotationSettings();

        Log("Registering elevated startup task");
        RegisterElevatedBackendTask();

        Log("Starting elevated backend");
        StartElevatedBackend();

        Log($"Rotations backup: {_settingsBackupPath}");
    }

    public async Task UninstallAsync()
    {
        Log("Backing up saved rotations");
        BackupRotationSettings();

        Log("Stopping running app processes");
        StopAppProcesses();

        Log("Removing elevated startup task");
        UnregisterElevatedBackendTask();
        StopElevatedBackendProcesses();

        Log("Removing elevated backend helper");
        RemoveElevatedBackendFiles();

        Log("Removing package");
        await RemoveInstalledPackagesAsync();

        Log($"Rotations were preserved at {_settingsBackupPath}");
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void BackupRotationSettings()
    {
        var package = GetInstalledPackage();
        if (package is null)
        {
            return;
        }

        var sourcePath = Path.Combine(GetLocalStatePath(package.Id.FamilyName), "rotation-settings.json");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(_backupDir);
        File.Copy(sourcePath, _settingsBackupPath, overwrite: true);
        Log($"Backed up rotations to {_settingsBackupPath}");
    }

    private void RestoreRotationSettings()
    {
        if (!File.Exists(_settingsBackupPath))
        {
            return;
        }

        var package = GetInstalledPackage();
        if (package is null)
        {
            return;
        }

        var localState = GetLocalStatePath(package.Id.FamilyName);
        Directory.CreateDirectory(localState);
        File.Copy(_settingsBackupPath, Path.Combine(localState, "rotation-settings.json"), overwrite: true);
        Log($"Restored rotations from {_settingsBackupPath}");
    }

    private static string GetLocalStatePath(string packageFamilyName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            packageFamilyName,
            "LocalState");
    }

    private void TrustCertificate()
    {
        var certificatePath = Path.Combine(_root, "RotationTracker.cer");
        if (!File.Exists(certificatePath))
        {
            throw new FileNotFoundException("Release certificate not found.", certificatePath);
        }

        using var certificate = new X509Certificate2(certificatePath);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
            .Count > 0;

        if (!existing)
        {
            store.Add(certificate);
        }
    }

    private static void StopAppProcesses()
    {
        var names = new[]
        {
            "RotationTracker",
            "RotationTracker.Backend",
            "GameBar",
            "GameBarFT",
            "GameBarFTServer",
            "GameBarElevatedFT_Player",
            "gamebarpresencewriter",
            "GamePanel"
        };

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
        }
    }

    private async Task InstallPackageAsync()
    {
        var bundlePath = Path.Combine(_root, "RotationTracker.msixbundle");
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException("Release bundle not found.", bundlePath);
        }

        var dependencyUris = GetReleaseDependencyPaths()
            .Select(path => new Uri(path))
            .ToList();

        var packageManager = new PackageManager();
        var result = await packageManager
            .AddPackageAsync(new Uri(bundlePath), dependencyUris, DeploymentOptions.ForceApplicationShutdown)
            .AsTask();

        ThrowIfDeploymentFailed(result, "install");
    }

    private async Task RemoveInstalledPackagesAsync()
    {
        var packageManager = new PackageManager();
        foreach (var package in GetInstalledPackages())
        {
            if (package?.Id?.FullName is not { Length: > 0 } packageFullName)
            {
                continue;
            }

            Log($"Removing package {packageFullName}");
            var result = await packageManager.RemovePackageAsync(package.Id.FullName).AsTask();
            ThrowIfDeploymentFailed(result, "remove");
        }
    }

    private IEnumerable<string> GetReleaseDependencyPaths()
    {
        var bundlePath = Path.Combine(_root, "RotationTracker.msixbundle");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(_root, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)))
        {
            if (path.Equals(bundlePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }
    }

    private Package? GetInstalledPackage()
    {
        return GetInstalledPackages()
            .OrderByDescending(package => package.Id.Version.Major)
            .ThenByDescending(package => package.Id.Version.Minor)
            .ThenByDescending(package => package.Id.Version.Build)
            .ThenByDescending(package => package.Id.Version.Revision)
            .FirstOrDefault();
    }

    private static IEnumerable<Package> GetInstalledPackages()
    {
        return new PackageManager()
            .FindPackagesForUser(string.Empty)
            .Where(package => package.Id.Name.StartsWith(PackageName, StringComparison.OrdinalIgnoreCase));
    }

    private void InstallElevatedBackendFiles()
    {
        var sourceDir = Path.Combine(_root, "ElevatedBackend");
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException("Elevated backend files were not found in the installer package.");
        }

        Directory.CreateDirectory(_backupDir);
        RunProcess("schtasks.exe", "/End /TN \"{0}\"", true, ScheduledTaskName);
        StopAppProcesses();
        StopElevatedBackendProcesses();

        Directory.CreateDirectory(_elevatedBackendDir);
        CopyDirectory(sourceDir, _elevatedBackendDir);

        if (!File.Exists(_elevatedBackendExePath))
        {
            throw new FileNotFoundException("Elevated backend executable was not copied.", _elevatedBackendExePath);
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            throw new InvalidOperationException("Installer executable path could not be resolved.");
        }

        CopyFileWithRetry(currentExe, _supervisorExePath);
    }

    private void RegisterElevatedBackendTask()
    {
        var package = GetInstalledPackage() ?? throw new InvalidOperationException("RotationTracker package is not installed.");
        var packageSid = AppContainerSidHelper.DeriveSidString(package.Id.FamilyName);

        var taskCommand = Quote(_supervisorExePath) +
            " --supervise-backend " +
            Quote(packageSid);

        RunProcess(
            "schtasks.exe",
            "/Create /TN \"{0}\" /SC ONLOGON /TR \"{1}\" /RL HIGHEST /F",
            ScheduledTaskName,
            taskCommand);
    }

    private void UnregisterElevatedBackendTask()
    {
        RunProcess("schtasks.exe", "/End /TN \"{0}\"", true, ScheduledTaskName);
        RunProcess("schtasks.exe", "/Delete /TN \"{0}\" /F", true, ScheduledTaskName);
    }

    private void RemoveElevatedBackendFiles()
    {
        if (Directory.Exists(_elevatedBackendDir))
        {
            DeleteDirectoryWithRetry(_elevatedBackendDir);
        }
    }

    private void StartElevatedBackend()
    {
        RunProcess("schtasks.exe", "/Run /TN \"{0}\"", ScheduledTaskName);
    }

    private void StopElevatedBackendProcesses()
    {
        var helperDir = Path.GetFullPath(_elevatedBackendDir);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath))
                {
                    continue;
                }

                var fullProcessPath = Path.GetFullPath(processPath);
                if (!fullProcessPath.StartsWith(helperDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Log($"Stopping helper process {process.ProcessName} ({process.Id})");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        const int attempts = 10;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch when (attempt < attempts)
            {
                Thread.Sleep(500);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFileWithRetry(file, destination);
        }
    }

    private static void CopyFileWithRetry(string sourceFile, string destinationFile)
    {
        const int attempts = 10;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.Copy(sourceFile, destinationFile, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(500);
            }
        }

        File.Copy(sourceFile, destinationFile, overwrite: true);
    }

    private static void ThrowIfDeploymentFailed(DeploymentResult? result, string operation)
    {
        if (result is null || result.ExtendedErrorCode is null || result.ExtendedErrorCode.HResult == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Package {operation} failed: {result.ErrorText} ({result.ExtendedErrorCode.HResult})");
    }

    private void RunProcess(string fileName, string argumentsFormat, params object[] arguments)
    {
        RunProcess(fileName, argumentsFormat, ignoreFailure: false, arguments);
    }

    private void RunProcess(string fileName, string argumentsFormat, bool ignoreFailure, params object[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Format(argumentsFormat, arguments),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Log(output);
        Log(error);

        if (!ignoreFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private void Log(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _log(message.TrimEnd());
        }
    }
}

internal static class BackendSupervisor
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            return 2;
        }

        var backendPath = args.Length >= 3
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "RotationTracker.Backend.exe");
        var packageSid = args.Length >= 3 ? args[2] : args[1];
        var logPath = Path.Combine(Path.GetDirectoryName(backendPath) ?? AppContext.BaseDirectory, "supervisor.log");

        while (true)
        {
            try
            {
                if (!File.Exists(backendPath))
                {
                    WriteLog(logPath, $"Backend missing: {backendPath}");
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                    continue;
                }

                var startedAt = DateTimeOffset.Now;
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = backendPath,
                    Arguments = Quote(packageSid),
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    WriteLog(logPath, "Failed to start backend.");
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                    continue;
                }

                WriteLog(logPath, $"Started backend pid={process.Id}");
                process.WaitForExit();
                var runtime = DateTimeOffset.Now - startedAt;
                WriteLog(logPath, $"Backend exited code={process.ExitCode} runtimeSeconds={runtime.TotalSeconds:0.0}");

                if (runtime < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Supervisor error: " + ex.Message);
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static void WriteLog(string logPath, string message)
    {
        try
        {
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} | {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

internal static class AppContainerSidHelper
{
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string DeriveSidString(string packageFamilyName)
    {
        var sidPtr = IntPtr.Zero;
        var sidStringPtr = IntPtr.Zero;
        try
        {
            var hr = DeriveAppContainerSidFromAppContainerName(packageFamilyName, out sidPtr);
            if (hr != 0 || sidPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("DeriveAppContainerSidFromAppContainerName failed: " + hr);
            }

            if (!ConvertSidToStringSid(sidPtr, out sidStringPtr) || sidStringPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("ConvertSidToStringSid failed.");
            }

            return Marshal.PtrToStringUni(sidStringPtr) ?? throw new InvalidOperationException("SID conversion returned null.");
        }
        finally
        {
            if (sidPtr != IntPtr.Zero)
            {
                LocalFree(sidPtr);
            }

            if (sidStringPtr != IntPtr.Zero)
            {
                LocalFree(sidStringPtr);
            }
        }
    }
}
