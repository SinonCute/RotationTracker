using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace RotationTracker.Backend
{
    /// <summary>
    /// Installs a WH_KEYBOARD_LL global keyboard hook and emits
    /// <see cref="PressCompleted"/> events when a watched key finishes being
    /// released. Short vs long press is decided by how long the key was held.
    /// </summary>
    internal sealed class KeyboardPoller
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int LongPressThresholdMs = 350;

        private readonly object _lock = new object();
        private Dictionary<int, string> _watched = new Dictionary<int, string>();
        private Dictionary<int, KeyState> _state = new Dictionary<int, KeyState>();
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private int _lastStartError;

        public event EventHandler<(string key, bool longPress)> PressCompleted;
        public event EventHandler<string> KeyUp;
        public bool IsInstalled => _hookId != IntPtr.Zero;
        public int LastStartError => _lastStartError;

        private sealed class KeyState
        {
            public bool Down;
            public Stopwatch DownTimer;
        }

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
                    _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
                }
            }
            catch
            {
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            }

            _lastStartError = _hookId == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            return _hookId != IntPtr.Zero;
        }

        public void Stop()
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        public void SetWatchedKeys(IEnumerable<string> keys)
        {
            var mapped = new Dictionary<int, string>();
            if (keys != null)
            {
                foreach (var raw in keys)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var key = raw.Trim();
                    var vk = MapKeyToVirtualKey(key);
                    if (vk == 0) continue;
                    mapped[vk] = key;
                }
            }

            lock (_lock)
            {
                _watched = mapped;
                var newState = new Dictionary<int, KeyState>();
                foreach (var kv in mapped)
                {
                    newState[kv.Key] = _state.TryGetValue(kv.Key, out var prev) ? prev : new KeyState();
                }
                _state = newState;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = unchecked((int)info.vkCode);
                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) ProcessVirtualKey(vk, true);
                else if (message == WM_KEYUP || message == WM_SYSKEYUP) ProcessVirtualKey(vk, false);
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void ProcessVirtualKey(int vk, bool isDown)
        {
            string keyName = null;
            bool shouldEmitPress = false;
            bool isLong = false;
            bool shouldEmitKeyUp = false;

            lock (_lock)
            {
                if (!_watched.TryGetValue(vk, out keyName) || !_state.TryGetValue(vk, out var ks))
                {
                    return;
                }

                if (isDown)
                {
                    if (!ks.Down)
                    {
                        ks.Down = true;
                        ks.DownTimer = Stopwatch.StartNew();
                    }
                }
                else
                {
                    if (ks.Down)
                    {
                        var held = ks.DownTimer?.ElapsedMilliseconds ?? 0;
                        isLong = held >= LongPressThresholdMs;
                        ks.Down = false;
                        ks.DownTimer = null;
                        shouldEmitPress = true;
                        shouldEmitKeyUp = true;
                    }
                }
            }

            if (shouldEmitPress)
            {
                try { PressCompleted?.Invoke(this, (keyName, isLong)); } catch { }
            }
            if (shouldEmitKeyUp)
            {
                try { KeyUp?.Invoke(this, keyName); } catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Maps a textual key (for example "1", "Q", "F5", "NUMPAD3") to a
        /// Windows virtual key code. Returns 0 when the input is unrecognized.
        /// </summary>
        private static int MapKeyToVirtualKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return 0;
            var k = key.Trim().ToUpperInvariant();

            if (k.Length == 1)
            {
                char c = k[0];
                if (c >= '0' && c <= '9') return 0x30 + (c - '0');
                if (c >= 'A' && c <= 'Z') return 0x41 + (c - 'A');
            }

            if (k.StartsWith("NUMPAD") && k.Length == 7)
            {
                char c = k[6];
                if (c >= '0' && c <= '9') return 0x60 + (c - '0');
            }

            if (k.StartsWith("F") && int.TryParse(k.Substring(1), out var fn) && fn >= 1 && fn <= 24)
            {
                return 0x70 + (fn - 1);
            }

            switch (k)
            {
                case "SPACE": return 0x20;
                case "ENTER": case "RETURN": return 0x0D;
                case "TAB": return 0x09;
                case "ESC": case "ESCAPE": return 0x1B;
                case "SHIFT": return 0x10;
                case "CTRL": case "CONTROL": return 0x11;
                case "ALT": return 0x12;
                case "LSHIFT": return 0xA0;
                case "RSHIFT": return 0xA1;
                case "LCTRL": return 0xA2;
                case "RCTRL": return 0xA3;
                case "LALT": return 0xA4;
                case "RALT": return 0xA5;
                case "UP": return 0x26;
                case "DOWN": return 0x28;
                case "LEFT": return 0x25;
                case "RIGHT": return 0x27;
                default: return 0;
            }
        }
    }
}
