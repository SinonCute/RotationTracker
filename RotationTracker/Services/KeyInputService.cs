using System;
using System.Collections.Generic;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace RotationTracker.Services
{
    /// <summary>
    /// Listens to widget window key presses and normalizes them to
    /// rotation keys (single character strings like "1", "Q").
    /// Only fires while the widget window has focus — matches the
    /// UWP sandbox capability, no full-trust required.
    /// </summary>
    public sealed class KeyInputService
    {
        private readonly CoreWindow _window;
        private bool _hooked;

        public event EventHandler<string> KeyPressed;
        public event EventHandler ActivationRequested;

        public KeyInputService(CoreWindow window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
        }

        public void Start()
        {
            if (_hooked || _window == null) return;
            _window.KeyDown += OnKeyDown;
            _hooked = true;
        }

        public void Stop()
        {
            if (!_hooked || _window == null) return;
            _window.KeyDown -= OnKeyDown;
            _hooked = false;
        }

        private void OnKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (IsActivationComboPressed(sender, args.VirtualKey))
            {
                App.BootstrapLog("[KeyInputService] Activation combo detected.");
                ActivationRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            var keyName = Normalize(args.VirtualKey);
            if (keyName == null) return;
            KeyPressed?.Invoke(this, keyName);
        }

        private static bool IsActivationComboPressed(CoreWindow window, VirtualKey key)
        {
            if (window == null) return false;
            bool isActivation = key == VirtualKey.F10;
            if (isActivation)
            {
                App.BootstrapLog("[KeyInputService] F10 pressed.");
            }

            return isActivation;
        }

        private static bool IsKeyDown(CoreWindow window, VirtualKey key)
        {
            return window.GetKeyState(key).HasFlag(CoreVirtualKeyStates.Down);
        }

        public static string Normalize(VirtualKey key)
        {
            // Digits on the top row.
            if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            {
                return ((int)(key - VirtualKey.Number0)).ToString();
            }

            // Letters.
            if (key >= VirtualKey.A && key <= VirtualKey.Z)
            {
                char c = (char)('A' + (key - VirtualKey.A));
                return c.ToString();
            }

            // Numpad digits.
            if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            {
                return ((int)(key - VirtualKey.NumberPad0)).ToString();
            }

            if (key >= VirtualKey.F1 && key <= VirtualKey.F24)
            {
                return "F" + ((int)(key - VirtualKey.F1) + 1).ToString();
            }

            return null;
        }

        public static bool KeyMatches(string configured, string pressed)
        {
            if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(pressed)) return false;
            return string.Equals(configured.Trim(), pressed.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
