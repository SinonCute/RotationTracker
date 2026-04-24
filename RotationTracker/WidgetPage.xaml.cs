using Microsoft.Gaming.XboxGameBar;
using RotationTracker.Models;
using RotationTracker.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace RotationTracker
{
    public sealed class StepViewModel : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush InactiveFill =
            new SolidColorBrush(Color.FromArgb(0x66, 0x2A, 0x2A, 0x2A));
        private static readonly SolidColorBrush InactiveStroke =
            new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush CurrentFill =
            new SolidColorBrush(Color.FromArgb(0xAA, 0x3B, 0x31, 0x20));
        private static readonly SolidColorBrush CurrentStroke =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66));
        private static readonly SolidColorBrush InactiveTagFill =
            new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x11));
        private static readonly SolidColorBrush CurrentTagFill =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD1, 0x66));
        private static readonly SolidColorBrush InactiveTagFg =
            new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush CurrentTagFg =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x13, 0x00));

        private bool _isCurrent;
        private string _iconUrl;
        private string _operatorAvatarUrl;
        private string _labelText = "";
        private string _keyHint = "";
        private bool _isLongPress;

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                Notify();
                Notify(nameof(BadgeFillBrush));
                Notify(nameof(BadgeStrokeBrush));
                Notify(nameof(KeyTagBrush));
                Notify(nameof(KeyTagForegroundBrush));
            }
        }

        public string IconUrl
        {
            get => _iconUrl;
            set
            {
                _iconUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                Notify();
                Notify(nameof(HasIcon));
            }
        }

        public string OperatorAvatarUrl
        {
            get => _operatorAvatarUrl;
            set
            {
                _operatorAvatarUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                Notify();
                Notify(nameof(HasAvatar));
            }
        }

        public string LabelText
        {
            get => _labelText;
            set
            {
                _labelText = value ?? "";
                Notify();
            }
        }

        public string KeyHint
        {
            get => _keyHint;
            set
            {
                _keyHint = value ?? "";
                Notify();
            }
        }

        public bool IsLongPress
        {
            get => _isLongPress;
            set
            {
                if (_isLongPress == value) return;
                _isLongPress = value;
                Notify();
            }
        }

        public bool HasIcon => !string.IsNullOrWhiteSpace(_iconUrl);
        public bool HasAvatar => !string.IsNullOrWhiteSpace(_operatorAvatarUrl);

        public SolidColorBrush BadgeFillBrush => _isCurrent ? CurrentFill : InactiveFill;
        public SolidColorBrush BadgeStrokeBrush => _isCurrent ? CurrentStroke : InactiveStroke;
        public SolidColorBrush KeyTagBrush => _isCurrent ? CurrentTagFill : InactiveTagFill;
        public SolidColorBrush KeyTagForegroundBrush => _isCurrent ? CurrentTagFg : InactiveTagFg;

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed partial class WidgetPage : Page, INotifyPropertyChanged
    {
        private XboxGameBarWidget _widget;
        private readonly RotationRuntime _runtime = new RotationRuntime();
        private KeyInputService _focusedKeyInput;
        private BackendClient _backend;
        private RotationSettings _settings;
        private bool _isRotationActive;
        private DateTimeOffset _lastToggleAt = DateTimeOffset.MinValue;

        public ObservableCollection<StepViewModel> Steps { get; } = new ObservableCollection<StepViewModel>();

        public string RotationName { get; private set; } = "Rotation Tracker";
        public string ProgressSummary { get; private set; } = "No active rotation";
        public string CurrentActionSummary { get; private set; } = "";
        public bool IsEmpty { get; private set; } = true;
        public bool ShowInactiveOverlay => !IsEmpty && !_isRotationActive;
        public double ContentOpacity => ShowInactiveOverlay ? 0.28 : 1.0;

        public event PropertyChangedEventHandler PropertyChanged;

        public WidgetPage()
        {
            InitializeComponent();
            Loaded += WidgetPage_Loaded;
            Unloaded += WidgetPage_Unloaded;
            _runtime.PropertyChanged += Runtime_PropertyChanged;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _widget = e.Parameter as XboxGameBarWidget ?? App.CurrentWidget;
            if (_widget != null)
            {
                _widget.PinnedChanged += Widget_PinnedChanged;
            }

            if (IsLoaded)
            {
                StartInputCapture();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopInputCapture();
            if (_widget != null)
            {
                _widget.PinnedChanged -= Widget_PinnedChanged;
            }
            base.OnNavigatedFrom(e);
        }

        private async void WidgetPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await OperatorCatalog.Instance.EnsureLoadedAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WidgetPage] OperatorCatalog load failed: {ex.Message}");
            }

            await SettingsWindow.RestoreIfNeededAsync(_widget ?? App.CurrentWidget, new Size(540, 220));
            ReloadFromSettings();
            ApplyPinnedVisuals();

            StartInputCapture();

            _backend = BackendClient.Instance;
            _backend.InputDetected += OnBackendInput;
            _backend.ConnectedEvent += Backend_Connected;

            SettingsService.Instance.SettingsChanged += OnSettingsChanged;

            try
            {
                await _backend.StartAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WidgetPage] Backend start failed: {ex.Message}");
            }
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ReloadFromSettings);
        }

        private void WidgetPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_focusedKeyInput != null)
            {
                StopInputCapture();
            }

            if (_backend != null)
            {
                _backend.InputDetected -= OnBackendInput;
                _backend.ConnectedEvent -= Backend_Connected;
                _backend = null;
            }

            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        }

        public void ReloadFromSettings()
        {
            _settings = SettingsService.Instance.Load();
            _runtime.LoopWhenComplete = _settings.LoopWhenComplete;

            var active = _settings.Rotations.FirstOrDefault(r => r.Id == _settings.ActiveRotationId)
                          ?? _settings.Rotations.FirstOrDefault();

            _runtime.Definition = active;
            SetRotationActive(false);
            RebuildSteps();
            UpdateHeaderState();
            RefreshBackendWatchSet();
            QueueEnsureResponsiveWindowSize();
        }

        private void RebuildSteps()
        {
            Steps.Clear();
            var definition = _runtime.Definition;
            if (definition?.Steps == null) return;

            for (int i = 0; i < definition.Steps.Count; i++)
            {
                Steps.Add(BuildStepViewModel(definition, definition.Steps[i], i == _runtime.CurrentStepIndex));
            }

            ScrollCurrentStepIntoView();
        }

        private StepViewModel BuildStepViewModel(RotationDefinition definition, RotationStep step, bool isCurrent)
        {
            var vm = new StepViewModel();
            var catalog = OperatorCatalog.Instance;

            string opId = "";
            if (definition?.OperatorSlots != null
                && step.SlotIndex >= 0
                && step.SlotIndex < definition.OperatorSlots.Count)
            {
                opId = definition.OperatorSlots[step.SlotIndex] ?? "";
            }

            var op = catalog.Get(opId);
            vm.OperatorAvatarUrl = op?.AvatarUrl;
            vm.IconUrl = op != null ? catalog.GetSkillIconUrl(opId, GetIconSlot(step.Action)) : null;

            var input = ActionInput.For(step.SlotIndex, step.Action);
            vm.KeyHint = input.ToHint();
            vm.IsLongPress = input.Kind == InputKind.LongPress;
            vm.LabelText = BuildStepLabel(step, op);
            vm.IsCurrent = isCurrent;
            return vm;
        }

        private static string BuildStepLabel(RotationStep step, OperatorInfo op)
        {
            var actionLabel = FormatAction(step.Action);
            if (op != null && !string.IsNullOrEmpty(op.DisplayName))
            {
                return $"{op.DisplayName} {actionLabel}";
            }

            return $"Slot {step.SlotIndex + 1} {actionLabel}";
        }

        private static string FormatAction(RotationAction action)
        {
            switch (action)
            {
                case RotationAction.Skill: return "Skill";
                case RotationAction.Combo: return "Combo";
                case RotationAction.Normal: return "Basic";
                case RotationAction.Ultimate: return "Ultimate";
                case RotationAction.FinalStrike: return "Final Strike";
                default: return action.ToString();
            }
        }

        private static string GetIconSlot(RotationAction action)
        {
            switch (action)
            {
                case RotationAction.FinalStrike:
                    // Reuse the basic-attack icon until the game data has a dedicated one.
                    return RotationAction.Normal.ToString();
                default:
                    return action.ToString();
            }
        }

        private void UpdateCurrentStepHighlights()
        {
            for (int i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsCurrent = i == _runtime.CurrentStepIndex;
            }

            ScrollCurrentStepIntoView();
        }

        private void ScrollCurrentStepIntoView()
        {
            if (StepsScroller == null || StepsItems == null || Steps.Count == 0) return;

            int currentIndex = _runtime.CurrentStepIndex;
            if (currentIndex < 0 || currentIndex >= Steps.Count) return;

            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                try
                {
                    StepsItems.UpdateLayout();
                    var container = StepsItems.ContainerFromIndex(currentIndex) as FrameworkElement;
                    if (container == null) return;

                    var transform = container.TransformToVisual(StepsItems);
                    var pos = transform.TransformPoint(new Point(0, 0));

                    double viewport = StepsScroller.ViewportWidth;
                    if (viewport <= 0) return;

                    double targetOffset = pos.X + (container.ActualWidth / 2.0) - (viewport / 2.0);
                    if (targetOffset < 0) targetOffset = 0;

                    double maxOffset = Math.Max(0, StepsScroller.ExtentWidth - viewport);
                    if (targetOffset > maxOffset) targetOffset = maxOffset;

                    StepsScroller.ChangeView(targetOffset, null, null, false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[WidgetPage] ScrollCurrentStepIntoView failed: {ex.Message}");
                }
            });
        }

        private void UpdateHeaderState()
        {
            RotationName = _runtime.Definition?.Name ?? "Rotation Tracker";
            IsEmpty = !_runtime.HasDefinition;

            var step = _runtime.CurrentStep;
            int totalSteps = _runtime.Definition?.Steps?.Count ?? 0;
            if (step != null && _runtime.Definition != null && totalSteps > 0)
            {
                ProgressSummary = $"Step {_runtime.CurrentStepIndex + 1} of {totalSteps}";
                var input = ActionInput.For(step.SlotIndex, step.Action);
                CurrentActionSummary = $"{FormatAction(step.Action)} / {input.ToHint()}";
            }
            else
            {
                ProgressSummary = totalSteps > 0 ? $"{totalSteps} steps ready" : "No active rotation";
                CurrentActionSummary = "";
            }

            Notify(nameof(RotationName));
            Notify(nameof(IsEmpty));
            Notify(nameof(ShowInactiveOverlay));
            Notify(nameof(ContentOpacity));
            Notify(nameof(ProgressSummary));
            Notify(nameof(CurrentActionSummary));
        }

        private void RefreshBackendWatchSet()
        {
            if (_backend == null) return;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "2", "3", "4", "E", "F10" };
            if (_runtime.Definition?.Steps != null)
            {
                foreach (var step in _runtime.Definition.Steps)
                {
                    var input = ActionInput.For(step.SlotIndex, step.Action);
                    if (input.Kind != InputKind.MouseLeft && !string.IsNullOrEmpty(input.Key))
                    {
                        keys.Add(input.Key);
                    }
                }
            }

            _backend.SetWatchedKeys(keys);
        }

        private void Backend_Connected(object sender, EventArgs e)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, RefreshBackendWatchSet);
        }

        private void Runtime_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RotationRuntime.CurrentStepIndex))
            {
                UpdateCurrentStepHighlights();
                UpdateHeaderState();
            }
            else if (e.PropertyName == nameof(RotationRuntime.Definition))
            {
                RebuildSteps();
                UpdateHeaderState();
                RefreshBackendWatchSet();
                QueueEnsureResponsiveWindowSize();
            }
        }

        private void OnFocusedKeyPressed(object sender, string keyName)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (string.Equals(keyName, "F10", StringComparison.OrdinalIgnoreCase))
                {
                    RequestToggleRotation("focused F10");
                    return;
                }
                if (_settings == null || !_settings.AutoAdvanceOnKey || !_isRotationActive) return;
                _runtime.TryAdvance(InputKind.ShortPress, keyName);
            });
        }

        private void OnBackendInput(object sender, InputEventArgs e)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (e.Kind == InputKind.ShortPress
                    && string.Equals(e.Key, "F10", StringComparison.OrdinalIgnoreCase))
                {
                    RequestToggleRotation("backend F10");
                    return;
                }
                if (_settings == null || !_settings.AutoAdvanceOnKey || !_isRotationActive) return;
                _runtime.TryAdvance(e.Kind, e.Key);
            });
        }

        private void OnActivationRequested(object sender, EventArgs e)
        {
            App.BootstrapLog("WidgetPage: activation requested.");
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => RequestToggleRotation("activation request"));
        }

        private void ActivateRotation()
        {
            if (!_runtime.HasDefinition)
            {
                App.BootstrapLog("WidgetPage: activation ignored (no definition).");
                return;
            }
            _runtime.Reset();
            SetRotationActive(true);
            UpdateCurrentStepHighlights();
            UpdateHeaderState();
            App.BootstrapLog("WidgetPage: rotation activated.");
        }

        private void ToggleRotationActive()
        {
            if (_isRotationActive)
            {
                _runtime.Reset();
                SetRotationActive(false);
                UpdateHeaderState();
                App.BootstrapLog("WidgetPage: rotation reset to inactive.");
                return;
            }

            ActivateRotation();
        }

        private void RequestToggleRotation(string source)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastToggleAt).TotalMilliseconds < 250)
            {
                App.BootstrapLog($"WidgetPage: ignored duplicate toggle from {source}.");
                return;
            }

            _lastToggleAt = now;
            App.BootstrapLog($"WidgetPage: toggle requested from {source}.");
            ToggleRotationActive();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            _runtime.Reset();
            SetRotationActive(false);
            UpdateHeaderState();
        }

        private async void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            App.BootstrapLog("WidgetPage: settings button clicked.");
            try
            {
                StopInputCapture();
                await SettingsWindow.ShowAsync();
                App.BootstrapLog("WidgetPage: SettingsWindow.ShowAsync returned.");
            }
            catch (Exception ex)
            {
                StartInputCapture();
                App.BootstrapLog("WidgetPage: Open settings failed.", ex);
            }
        }

        private void Widget_PinnedChanged(XboxGameBarWidget sender, object args)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyPinnedVisuals);
        }

        private void ApplyPinnedVisuals()
        {
            bool pinned = _widget?.Pinned == true;
            double opacity = _settings?.PinnedOpacity ?? 0.85;
            if (opacity < 0.1) opacity = 0.1;
            if (opacity > 1.0) opacity = 1.0;

            if (pinned)
            {
                CardBorder.Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(opacity * 255), 0x14, 0x14, 0x14));
            }
            else
            {
                CardBorder.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x1F, 0x1F));
            }
        }

        private void QueueEnsureResponsiveWindowSize()
        {
            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                await EnsureResponsiveWindowSizeAsync();
            });
        }

        private async Task EnsureResponsiveWindowSizeAsync()
        {
            var widget = _widget ?? App.CurrentWidget;
            if (widget == null || _runtime.Definition?.Steps == null) return;

            int stepCount = Math.Max(1, _runtime.Definition.Steps.Count);
            int baselineVisibleSteps = Math.Min(6, stepCount);
            double desiredWidth = 180 + (baselineVisibleSteps * 112) + (Math.Max(0, baselineVisibleSteps - 1) * 18);
            if (desiredWidth < 420) desiredWidth = 420;
            if (desiredWidth > 1320) desiredWidth = 1320;

            double desiredHeight = stepCount >= 7 ? 300 : 250;

            var bounds = Window.Current?.Bounds ?? default;
            double currentWidth = bounds.Width > 0 ? bounds.Width : 0;
            double currentHeight = bounds.Height > 0 ? bounds.Height : 0;
            double targetWidth = currentWidth < desiredWidth ? desiredWidth : currentWidth;
            double targetHeight = currentHeight < desiredHeight ? desiredHeight : currentHeight;

            if (Math.Abs(targetWidth - currentWidth) < 1 && Math.Abs(targetHeight - currentHeight) < 1)
            {
                return;
            }

            try
            {
                await widget.TryResizeWindowAsync(new Size(targetWidth, targetHeight));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WidgetPage] EnsureResponsiveWindowSizeAsync failed: {ex.Message}");
            }
        }

        private void SetRotationActive(bool isActive)
        {
            if (_isRotationActive == isActive) return;
            _isRotationActive = isActive;
            Notify(nameof(ShowInactiveOverlay));
            Notify(nameof(ContentOpacity));
        }

        private void StartInputCapture()
        {
            if (_focusedKeyInput != null) return;

            _focusedKeyInput = new KeyInputService(Window.Current.CoreWindow);
            _focusedKeyInput.KeyPressed += OnFocusedKeyPressed;
            _focusedKeyInput.ActivationRequested += OnActivationRequested;
            _focusedKeyInput.Start();
        }

        private void StopInputCapture()
        {
            if (_focusedKeyInput == null) return;

            _focusedKeyInput.KeyPressed -= OnFocusedKeyPressed;
            _focusedKeyInput.ActivationRequested -= OnActivationRequested;
            _focusedKeyInput.Stop();
            _focusedKeyInput = null;
        }

        private void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
