using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RotationTracker.Backend
{
    internal sealed class MouseHookEventArgs : EventArgs
    {
        public int X { get; }
        public int Y { get; }

        public MouseHookEventArgs(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Installs a WH_MOUSE_LL low-level global mouse hook so left-click
    /// events are captured even while a fullscreen game has focus. This is
    /// needed because <c>GetAsyncKeyState(VK_LBUTTON)</c> polling can miss
    /// short clicks and does not always reflect game-owned mouse state.
    ///
    /// The hook must be installed on a thread that has a message pump.
    /// </summary>
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        // Keep the delegate alive so the CLR does not GC the callback while
        // native code still holds the function pointer.
        private LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly Action<string> _log;
        private int _clickCount;

        public event EventHandler<MouseHookEventArgs> LeftButtonDown;
        public event EventHandler LeftButtonUp;

        public MouseHook(Action<string> log = null)
        {
            _log = log;
        }

        public bool IsInstalled => _hookId != IntPtr.Zero;

        public bool Start()
        {
            if (_hookId != IntPtr.Zero) return true;

            _proc = HookCallback;

            try
            {
                using (var process = Process.GetCurrentProcess())
                using (var module = process.MainModule)
                {
                    var hMod = GetModuleHandle(module.ModuleName);
                    _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[MouseHook] Start threw: {ex.Message}");
                return false;
            }

            if (_hookId == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                _log?.Invoke($"[MouseHook] SetWindowsHookEx failed. win32_err={err}");
                return false;
            }

            _log?.Invoke("[MouseHook] Installed WH_MOUSE_LL hook.");
            return true;
        }

        public void Stop()
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _log?.Invoke("[MouseHook] Uninstalled hook.");
        }

        public void Dispose()
        {
            Stop();
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN)
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int n = Interlocked.Increment(ref _clickCount);
                    // Log the first few so the user can confirm the hook fires,
                    // then thin out the logging to avoid spamming backend.log.
                    if (n <= 5 || n % 25 == 0)
                    {
                        _log?.Invoke($"[MouseHook] left-down #{n}");
                    }
                    try { LeftButtonDown?.Invoke(this, new MouseHookEventArgs(ms.pt.x, ms.pt.y)); } catch { }
                }
                else if (msg == WM_LBUTTONUP)
                {
                    try { LeftButtonUp?.Invoke(this, EventArgs.Empty); } catch { }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
