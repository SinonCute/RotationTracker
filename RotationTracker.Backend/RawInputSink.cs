using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RotationTracker.Backend
{
    internal sealed class RawInputSink : NativeWindow, IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const int RIM_TYPEMOUSE = 0;
        private const int RIM_TYPEKEYBOARD = 1;
        private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
        private const ushort RI_KEY_BREAK = 0x0001;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;

        private readonly Action<string> _log;
        private bool _registered;

        public event EventHandler<int> KeyDown;
        public event EventHandler<int> KeyUp;
        public event EventHandler LeftButtonDown;
        public event EventHandler LeftButtonUp;

        public RawInputSink(Action<string> log = null)
        {
            _log = log;
            CreateHandle(new CreateParams());
            _registered = Register(Handle);
            _log?.Invoke(_registered
                ? "[RawInput] Registered keyboard+mouse input sink."
                : $"[RawInput] RegisterRawInputDevices failed. win32_err={Marshal.GetLastWin32Error()}");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        private void HandleRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
                var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

                if (raw.header.dwType == RIM_TYPEKEYBOARD)
                {
                    int vk = raw.data.keyboard.VKey;
                    if (vk != 0)
                    {
                        bool isBreak = (raw.data.keyboard.Flags & RI_KEY_BREAK) != 0;
                        if (isBreak) KeyUp?.Invoke(this, vk);
                        else KeyDown?.Invoke(this, vk);
                    }
                }
                else if (raw.header.dwType == RIM_TYPEMOUSE)
                {
                    if ((raw.data.mouse.usButtonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0)
                    {
                        LeftButtonDown?.Invoke(this, EventArgs.Empty);
                    }
                    if ((raw.data.mouse.usButtonFlags & RI_MOUSE_LEFT_BUTTON_UP) != 0)
                    {
                        LeftButtonUp?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch
            {
                // Ignore malformed packets.
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        private static bool Register(IntPtr hwnd)
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02, // mouse
                    dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                    hwndTarget = hwnd
                },
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x06, // keyboard
                    dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                    hwndTarget = hwnd
                }
            };

            return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public int dwType;
            public int dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct RAWINPUTUNION
        {
            [FieldOffset(0)] public RAWMOUSE mouse;
            [FieldOffset(0)] public RAWKEYBOARD keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWINPUTUNION data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public uint ulButtons;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);
    }
}
