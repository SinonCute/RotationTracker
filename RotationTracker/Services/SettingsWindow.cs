using Microsoft.Gaming.XboxGameBar;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RotationTracker.Services
{
    /// <summary>
    /// Opens the settings UI inline inside the widget's own Frame, and
    /// temporarily grows the Game Bar widget window so the editor has room.
    /// The original size is restored when the user returns to the widget.
    /// </summary>
    public static class SettingsWindow
    {
        private const string EditorOpenKey = "SettingsWindow.EditorOpen";
        private const string SavedWidthKey = "SettingsWindow.SavedWidth";
        private const string SavedHeightKey = "SettingsWindow.SavedHeight";
        private static readonly Size DefaultWidgetSize = new Size(540, 220);

        // Desired size for editing; will be clamped to the widget's configured
        // MaxWindowSize by TryResizeWindowAsync.
        private static readonly Size EditorSize = new Size(1180, 820);

        private static Size? _savedSize;

        public static bool HasPendingRestore
        {
            get
            {
                try
                {
                    var values = ApplicationData.Current?.LocalSettings?.Values;
                    return values != null
                        && values.TryGetValue(EditorOpenKey, out var isOpen)
                        && isOpen is bool b
                        && b;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static async Task ShowAsync()
        {
            var frame = Window.Current?.Content as Frame;
            if (frame == null) return;

            // Remember the widget's current size so we can restore it on close.
            var widget = App.CurrentWidget;
            if (widget != null)
            {
                _savedSize = CaptureCurrentWidgetSize();
                PersistSavedSize(_savedSize.Value);
                try
                {
                    await widget.TryResizeWindowAsync(EditorSize);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[SettingsWindow] TryResizeWindowAsync(editor) failed: {ex.Message}");
                }
            }

            SetEditorOpen(true);

            // Avoid stacking duplicate settings entries in the back stack.
            if (frame.CurrentSourcePageType != typeof(SettingsPage))
            {
                frame.Navigate(typeof(SettingsPage));
            }
        }

        /// <summary>
        /// Navigates back to the widget and restores its original window size.
        /// </summary>
        public static async void CloseCurrent()
        {
            var frame = Window.Current?.Content as Frame;
            if (frame == null) return;

            var restoreTarget = TryGetSavedSize(out var savedSize) ? savedSize : DefaultWidgetSize;
            SetEditorOpen(false);

            if (frame.CanGoBack)
            {
                frame.GoBack();
            }
            else
            {
                frame.Navigate(typeof(WidgetPage), App.CurrentWidget);
            }

            var widget = App.CurrentWidget;
            await RestoreWidgetWindowAsync(widget, restoreTarget);
            ClearSavedSize();
        }

        public static async Task RestoreIfNeededAsync(XboxGameBarWidget widget, Size fallback)
        {
            if (!HasPendingRestore) return;
            await RestoreWidgetWindowAsync(widget, fallback);
            ClearPersistedState();
        }

        private static Size CaptureCurrentWidgetSize()
        {
            try
            {
                var bounds = Window.Current?.Bounds ?? default;
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    return new Size(bounds.Width, bounds.Height);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsWindow] CaptureCurrentWidgetSize failed: {ex.Message}");
            }
            // Fallback to the default widget size declared in the manifest.
            return DefaultWidgetSize;
        }

        private static async Task RestoreWidgetWindowAsync(XboxGameBarWidget widget, Size fallback)
        {
            if (widget == null) return;

            var target = TryGetSavedSize(out var savedSize) ? savedSize : fallback;
            try
            {
                await widget.TryResizeWindowAsync(target);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SettingsWindow] TryResizeWindowAsync(restore) failed: {ex.Message}");
            }
        }

        private static bool TryGetSavedSize(out Size size)
        {
            if (_savedSize.HasValue)
            {
                size = _savedSize.Value;
                return true;
            }

            try
            {
                var values = ApplicationData.Current?.LocalSettings?.Values;
                if (values != null
                    && values.TryGetValue(SavedWidthKey, out var widthRaw)
                    && values.TryGetValue(SavedHeightKey, out var heightRaw)
                    && widthRaw is double width
                    && heightRaw is double height
                    && width > 0
                    && height > 0)
                {
                    size = new Size(width, height);
                    return true;
                }
            }
            catch
            {
            }

            size = default;
            return false;
        }

        private static void PersistSavedSize(Size size)
        {
            try
            {
                var values = ApplicationData.Current?.LocalSettings?.Values;
                if (values == null) return;
                values[SavedWidthKey] = size.Width;
                values[SavedHeightKey] = size.Height;
            }
            catch
            {
            }
        }

        private static void SetEditorOpen(bool isOpen)
        {
            try
            {
                var values = ApplicationData.Current?.LocalSettings?.Values;
                if (values == null) return;
                values[EditorOpenKey] = isOpen;
            }
            catch
            {
            }
        }

        private static void ClearSavedSize()
        {
            _savedSize = null;

            try
            {
                var values = ApplicationData.Current?.LocalSettings?.Values;
                if (values == null) return;
                values.Remove(SavedWidthKey);
                values.Remove(SavedHeightKey);
            }
            catch
            {
            }
        }

        private static void ClearPersistedState()
        {
            SetEditorOpen(false);
            ClearSavedSize();
        }
    }
}
