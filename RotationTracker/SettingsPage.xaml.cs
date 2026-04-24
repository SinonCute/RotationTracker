using RotationTracker.Models;
using RotationTracker.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.UI.Core;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RotationTracker
{
    public sealed class TeamSlotEditorViewModel : INotifyPropertyChanged
    {
        private readonly int _slot;
        private readonly ObservableCollection<string> _target;
        private OperatorInfo _selected;

        public IReadOnlyList<OperatorInfo> Catalog { get; }

        public TeamSlotEditorViewModel(int slot, ObservableCollection<string> target, IReadOnlyList<OperatorInfo> catalog)
        {
            _slot = slot;
            _target = target;
            Catalog = catalog;

            if (target != null && slot < target.Count)
            {
                var id = target[slot];
                _selected = catalog.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public int Slot => _slot;
        public string SlotLabel => (_slot + 1).ToString();
        public string PlaceholderText => $"Slot {SlotLabel}";

        public OperatorInfo Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                if (_target != null && _slot < _target.Count)
                {
                    _target[_slot] = value?.Id ?? "";
                }
                Notify();
                Notify(nameof(SelectedOperatorId));
                Notify(nameof(AvatarUrl));
                Notify(nameof(HasAvatar));
                Notify(nameof(Display));
                Notify(nameof(OperatorDisplayName));
            }
        }

        public string SelectedOperatorId
        {
            get => _selected?.Id ?? "";
            set
            {
                var next = string.IsNullOrWhiteSpace(value)
                    ? null
                    : Catalog.FirstOrDefault(o => string.Equals(o.Id, value, StringComparison.OrdinalIgnoreCase));

                if (_selected == next) return;
                Selected = next;
            }
        }

        public string AvatarUrl => string.IsNullOrWhiteSpace(_selected?.AvatarUrl) ? null : _selected.AvatarUrl;
        public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

        public string OperatorDisplayName =>
            _selected != null && !string.IsNullOrEmpty(_selected.DisplayName)
                ? _selected.DisplayName
                : "Unassigned";

        public string Display => $"Slot {_slot + 1} - {OperatorDisplayName}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ActionOption
    {
        public int Value { get; set; }
        public string Label { get; set; } = "";
    }

    public sealed class StepEditorViewModel : INotifyPropertyChanged
    {
        private static readonly ActionOption[] DefaultActionChoices =
        {
            new ActionOption { Value = (int)RotationAction.Skill, Label = "Skill" },
            new ActionOption { Value = (int)RotationAction.Combo, Label = "Combo" },
            new ActionOption { Value = (int)RotationAction.Normal, Label = "Basic (click)" },
            new ActionOption { Value = (int)RotationAction.Ultimate, Label = "Ultimate (hold)" },
            new ActionOption { Value = (int)RotationAction.FinalStrike, Label = "Final Strike" },
        };

        private readonly RotationDefinition _rotation;
        private readonly RotationStep _step;
        private readonly ObservableCollection<TeamSlotEditorViewModel> _teamSlots;
        private int _order;

        public IReadOnlyList<ActionOption> ActionChoices => DefaultActionChoices;

        public ObservableCollection<TeamSlotEditorViewModel> TeamSlots => _teamSlots;

        public StepEditorViewModel(
            int order,
            RotationDefinition rotation,
            RotationStep step,
            ObservableCollection<TeamSlotEditorViewModel> teamSlots)
        {
            _order = order;
            _rotation = rotation;
            _step = step;
            _teamSlots = teamSlots;
        }

        public RotationStep Step => _step;

        public int Order
        {
            get => _order;
            set { _order = value; Notify(); Notify(nameof(OrderText)); }
        }

        public string OrderText => $"{_order + 1}.";

        public int SlotIndex
        {
            get => _step.SlotIndex;
            set
            {
                if (_step.SlotIndex == value) return;
                _step.SlotIndex = value;
                Notify();
                Notify(nameof(OperatorName));
            }
        }

        public int ActionValue
        {
            get => (int)_step.Action;
            set
            {
                var newAction = IntToAction(value);
                if (_step.Action == newAction) return;
                _step.Action = newAction;
                Notify();
            }
        }

        public string OperatorName
        {
            get
            {
                var op = ResolveOperator();
                if (op != null && !string.IsNullOrEmpty(op.DisplayName)) return op.DisplayName;
                return $"Slot {_step.SlotIndex + 1}";
            }
        }

        private OperatorInfo ResolveOperator()
        {
            if (_rotation == null || _rotation.OperatorSlots == null) return null;
            if (_step.SlotIndex < 0 || _step.SlotIndex >= _rotation.OperatorSlots.Count) return null;
            var id = _rotation.OperatorSlots[_step.SlotIndex];
            return OperatorCatalog.Instance.Get(id);
        }

        private static RotationAction IntToAction(int i)
        {
            switch (i)
            {
                case 0: return RotationAction.Skill;
                case 1: return RotationAction.Combo;
                case 2: return RotationAction.Normal;
                case 3: return RotationAction.Ultimate;
                case 4: return RotationAction.FinalStrike;
                default: return RotationAction.Skill;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed partial class SettingsPage : Page, INotifyPropertyChanged
    {
        private RotationSettings _working;
        private RotationDefinition _selected;
        private bool _suppressEvents;
        private XboxGameBarWidget _widget;
        private bool _closingForPinnedOnly;
        private ObservableCollection<TeamSlotEditorViewModel> _teamSlots =
            new ObservableCollection<TeamSlotEditorViewModel>();
        private ObservableCollection<StepEditorViewModel> _stepEditors =
            new ObservableCollection<StepEditorViewModel>();

        public ObservableCollection<RotationDefinition> Rotations => _working?.Rotations;
        public ObservableCollection<TeamSlotEditorViewModel> TeamSlots
        {
            get => _teamSlots;
            private set
            {
                _teamSlots = value ?? new ObservableCollection<TeamSlotEditorViewModel>();
                Notify();
            }
        }

        public ObservableCollection<StepEditorViewModel> StepEditors
        {
            get => _stepEditors;
            private set
            {
                _stepEditors = value ?? new ObservableCollection<StepEditorViewModel>();
                Notify();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _widget = App.CurrentWidget;
            if (_widget != null)
            {
                _widget.GameBarDisplayModeChanged += Widget_GameBarDisplayModeChanged;
            }
            _ = InitializeFromStorageAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_widget != null)
            {
                _widget.GameBarDisplayModeChanged -= Widget_GameBarDisplayModeChanged;
                _widget = null;
            }

            base.OnNavigatedFrom(e);
        }

        private async Task InitializeFromStorageAsync()
        {
            _suppressEvents = true;
            try
            {
                try
                {
                    await OperatorCatalog.Instance.EnsureLoadedAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[SettingsPage] OperatorCatalog load failed: {ex.Message}");
                }

                _working = CloneSettings(SettingsService.Instance.Load());

                if (_working.Rotations.Count == 0)
                {
                    _working.Rotations.Add(new RotationDefinition { Name = "New Team" });
                    _working.ActiveRotationId = _working.Rotations[0].Id;
                }

                Notify(nameof(Rotations));
                var initial = _working.Rotations.FirstOrDefault(r => r.Id == _working.ActiveRotationId)
                              ?? _working.Rotations.FirstOrDefault();

                AutoAdvanceToggle.IsOn = _working.AutoAdvanceOnKey;
                LoopToggle.IsOn = _working.LoopWhenComplete;
                OpacitySlider.Value = _working.PinnedOpacity;
                SelectRotation(initial, true);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void BindSelectedRotation()
        {
            foreach (var slot in TeamSlots.ToList())
            {
                slot.PropertyChanged -= OnSlotSelectionChanged;
            }

            var nextTeamSlots = new ObservableCollection<TeamSlotEditorViewModel>();
            if (_selected == null)
            {
                TeamSlots = nextTeamSlots;
                StepEditors = new ObservableCollection<StepEditorViewModel>();
                return;
            }

            var catalog = OperatorCatalog.Instance.All;

            for (int i = 0; i < RotationDefinition.SlotCount; i++)
            {
                var slotVm = new TeamSlotEditorViewModel(i, _selected.OperatorSlots, catalog);
                slotVm.PropertyChanged += OnSlotSelectionChanged;
                nextTeamSlots.Add(slotVm);
            }

            TeamSlots = nextTeamSlots;
            StepEditors = BuildStepEditors(nextTeamSlots);
        }

        private ObservableCollection<StepEditorViewModel> BuildStepEditors(
            ObservableCollection<TeamSlotEditorViewModel> teamSlots)
        {
            var editors = new ObservableCollection<StepEditorViewModel>();
            if (_selected == null)
            {
                return editors;
            }

            for (int i = 0; i < _selected.Steps.Count; i++)
            {
                editors.Add(new StepEditorViewModel(i, _selected, _selected.Steps[i], teamSlots));
            }

            return editors;
        }

        private void OnSlotSelectionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TeamSlotEditorViewModel.Selected)
                && e.PropertyName != nameof(TeamSlotEditorViewModel.SelectedOperatorId)) return;
            StepEditors = BuildStepEditors(TeamSlots);
        }

        private void OnRotationSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var selected = RotationSelector.SelectedItem as RotationDefinition;
            if (ReferenceEquals(_selected, selected)) return;
            SelectRotation(selected, true);
        }

        private async void OnAddRotationClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Add rotation",
                Content = "Create a blank rotation or import one from a share string.",
                PrimaryButtonText = "Create new",
                SecondaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                AddRotation(CreateNewRotation());
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ImportRotationAsync();
            }
        }

        private void OnRenameRotationClick(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _ = RenameSelectedRotationAsync();
        }

        private async void OnDeleteRotationClick(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            if (_working.Rotations.Count <= 1)
            {
                await ShowAlertAsync("At least one rotation is required.");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Delete rotation",
                Content = $"Remove '{_selected.Name}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            _suppressEvents = true;
            try
            {
                bool wasActive = _selected.Id == _working.ActiveRotationId;
                _working.Rotations.Remove(_selected);
                _selected = _working.Rotations.FirstOrDefault();
                if (wasActive)
                {
                    _working.ActiveRotationId = _selected?.Id ?? "";
                }
                SelectRotation(_selected, true);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private async void OnShareRotationClick(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            try
            {
                var payload = SettingsService.ExportRotation(_selected);
                var dataPackage = new DataPackage();
                dataPackage.SetText(payload);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                await ShowAlertAsync("Rotation share string copied to clipboard.");
            }
            catch (Exception ex)
            {
                App.BootstrapLog("[SettingsPage] Failed to copy rotation share string.", ex);
                await ShowAlertAsync("Failed to copy rotation share string.");
            }
        }

        private void SelectRotation(RotationDefinition rotation, bool rebind)
        {
            _suppressEvents = true;
            try
            {
                _selected = rotation;
                if (rebind) BindSelectedRotation();
            }
            finally
            {
                _suppressEvents = false;
            }

            QueueRotationSelectorSelection(rotation);
        }

        private void ApplyRotationSelectorSelection(RotationDefinition rotation)
        {
            if (RotationSelector == null)
            {
                return;
            }

            var rotations = _working?.Rotations;
            int selectedIndex = -1;

            if (rotation != null && rotations != null)
            {
                selectedIndex = rotations.IndexOf(rotation);
            }

            RotationSelector.SelectedIndex = selectedIndex;
        }

        private void QueueRotationSelectorSelection(RotationDefinition rotation)
        {
            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _suppressEvents = true;
                try
                {
                    ApplyRotationSelectorSelection(rotation);
                }
                finally
                {
                    _suppressEvents = false;
                }
            });

            if (ignored == null)
            {
                ApplyRotationSelectorSelection(rotation);
            }
        }

        private void OnAddStepClick(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var step = new RotationStep { SlotIndex = 0, Action = RotationAction.Skill };
            _selected.Steps.Add(step);
            StepEditors.Add(new StepEditorViewModel(StepEditors.Count, _selected, step, TeamSlots));
        }

        private void OnStepRemove(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is StepEditorViewModel vm)) return;
            if (_selected == null) return;

            _selected.Steps.Remove(vm.Step);
            StepEditors.Remove(vm);
            RenumberSteps();
        }

        private void OnStepMoveUp(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is StepEditorViewModel vm)) return;
            int i = StepEditors.IndexOf(vm);
            if (i <= 0) return;

            StepEditors.Move(i, i - 1);
            _selected.Steps.Move(i, i - 1);
            RenumberSteps();
        }

        private void OnStepMoveDown(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is StepEditorViewModel vm)) return;
            int i = StepEditors.IndexOf(vm);
            if (i < 0 || i >= StepEditors.Count - 1) return;

            StepEditors.Move(i, i + 1);
            _selected.Steps.Move(i, i + 1);
            RenumberSteps();
        }

        private void RenumberSteps()
        {
            for (int i = 0; i < StepEditors.Count; i++)
            {
                StepEditors[i].Order = i;
            }
        }

        private void OnAutoAdvanceToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _working == null) return;
            _working.AutoAdvanceOnKey = AutoAdvanceToggle.IsOn;
        }

        private void OnLoopToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _working == null) return;
            _working.LoopWhenComplete = LoopToggle.IsOn;
        }

        private void OnOpacityChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressEvents || _working == null) return;
            _working.PinnedOpacity = OpacitySlider.Value;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_working == null) return;

            SynchronizeCurrentEditorState();

            if (_selected != null)
            {
                _working.ActiveRotationId = _selected.Id;
            }
            else if (string.IsNullOrEmpty(_working.ActiveRotationId)
                || _working.Rotations.All(r => r.Id != _working.ActiveRotationId))
            {
                _working.ActiveRotationId = _working.Rotations.FirstOrDefault()?.Id ?? "";
            }

            SettingsService.Instance.Save(_working);
            SettingsWindow.CloseCurrent();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            SettingsWindow.CloseCurrent();
        }

        private void OnTeamSlotOperatorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox combo) || !(combo.DataContext is TeamSlotEditorViewModel vm)) return;

            vm.Selected = combo.SelectedItem as OperatorInfo;
            if (_selected?.OperatorSlots != null
                && vm.Slot >= 0
                && vm.Slot < _selected.OperatorSlots.Count)
            {
                _selected.OperatorSlots[vm.Slot] = vm.SelectedOperatorId ?? "";
            }

            App.BootstrapLog($"[SettingsPage] Slot {vm.Slot + 1} changed to '{vm.SelectedOperatorId ?? ""}' on rotation '{_selected?.Name ?? ""}'.");
        }
        private async Task ShowAlertAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Rotation Tracker",
                Content = message,
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            await dialog.ShowAsync();
        }

        private void SynchronizeCurrentEditorState()
        {
            if (_selected == null) return;

            if (_selected.OperatorSlots != null)
            {
                for (int i = 0; i < Math.Min(_selected.OperatorSlots.Count, TeamSlots.Count); i++)
                {
                    _selected.OperatorSlots[i] = TeamSlots[i].SelectedOperatorId ?? "";
                }
            }

            var slots = _selected.OperatorSlots == null ? "(null)" : string.Join(",", _selected.OperatorSlots);
            App.BootstrapLog($"[SettingsPage] Synchronize editor. rotation='{_selected.Name}' slots={slots}");
        }

        private static RotationSettings CloneSettings(RotationSettings source)
        {
            var clone = new RotationSettings
            {
                ActiveRotationId = source.ActiveRotationId,
                PinnedOpacity = source.PinnedOpacity,
                AutoAdvanceOnKey = source.AutoAdvanceOnKey,
                LoopWhenComplete = source.LoopWhenComplete,
            };

            foreach (var rotation in source.Rotations)
            {
                var copy = new RotationDefinition
                {
                    Id = rotation.Id,
                    Name = rotation.Name,
                };
                copy.OperatorSlots = new ObservableCollection<string>(rotation.OperatorSlots ?? new ObservableCollection<string>());
                while (copy.OperatorSlots.Count < RotationDefinition.SlotCount) copy.OperatorSlots.Add("");
                copy.Steps = new ObservableCollection<RotationStep>();
                if (rotation.Steps != null)
                {
                    foreach (var step in rotation.Steps)
                    {
                        copy.Steps.Add(new RotationStep
                        {
                            SlotIndex = step.SlotIndex,
                            Action = step.Action,
                            LabelOverride = step.LabelOverride,
                        });
                    }
                }
                clone.Rotations.Add(copy);
            }

            return clone;
        }

        private string NextRotationName()
        {
            const string baseName = "New Team";
            if (_working?.Rotations == null || _working.Rotations.Count == 0)
            {
                return baseName;
            }

            var names = new HashSet<string>(_working.Rotations
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            if (!names.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                var candidate = $"{baseName} {i}";
                if (!names.Contains(candidate))
                {
                    return candidate;
                }
            }

            return $"{baseName} {Guid.NewGuid():N}";
        }

        private void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private async Task RenameSelectedRotationAsync()
        {
            if (_selected == null)
            {
                return;
            }

            var editor = new TextBox
            {
                Text = _selected.Name ?? "",
                PlaceholderText = "Enter a rotation name",
                MinWidth = 320,
            };

            var dialog = new ContentDialog
            {
                Title = "Rename rotation",
                Content = editor,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                editor.Focus(FocusState.Programmatic);
                editor.SelectAll();
            });

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var nextName = editor.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(nextName))
            {
                await ShowAlertAsync("Rotation name cannot be empty.");
                return;
            }

            _selected.Name = nextName;
            Notify(nameof(Rotations));
            QueueRotationSelectorSelection(_selected);
            App.BootstrapLog($"[SettingsPage] Renamed rotation to '{nextName}'.");
        }

        private RotationDefinition CreateNewRotation()
        {
            return new RotationDefinition
            {
                Name = NextRotationName(),
                Steps = new ObservableCollection<RotationStep>
                {
                    new RotationStep { SlotIndex = 0, Action = RotationAction.Skill },
                },
            };
        }

        private void AddRotation(RotationDefinition rotation)
        {
            if (rotation == null || _working == null)
            {
                return;
            }

            _suppressEvents = true;
            try
            {
                if (string.IsNullOrWhiteSpace(rotation.Name))
                {
                    rotation.Name = NextRotationName();
                }

                _working.Rotations.Add(rotation);
                if (string.IsNullOrEmpty(_working.ActiveRotationId))
                {
                    _working.ActiveRotationId = rotation.Id;
                }

                Notify(nameof(Rotations));
                SelectRotation(rotation, true);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private async Task ImportRotationAsync()
        {
            var editor = new TextBox
            {
                PlaceholderText = "Paste a shared rotation string",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 360,
                Height = 140,
            };

            try
            {
                editor.Text = await Clipboard.GetContent().GetTextAsync();
                editor.SelectAll();
            }
            catch
            {
            }

            var dialog = new ContentDialog
            {
                Title = "Import rotation",
                Content = editor,
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                editor.Focus(FocusState.Programmatic);
            });

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!SettingsService.TryImportRotation(editor.Text, out var imported, out var error))
            {
                await ShowAlertAsync(error ?? "Invalid rotation share string.");
                return;
            }

            imported.Name = MakeImportedRotationName(imported.Name);
            AddRotation(imported);
            App.BootstrapLog($"[SettingsPage] Imported rotation '{imported.Name}'.");
        }

        private string MakeImportedRotationName(string name)
        {
            var baseName = string.IsNullOrWhiteSpace(name) ? "Imported Rotation" : name.Trim();
            var existing = new HashSet<string>(_working?.Rotations?
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                var candidate = $"{baseName} {i}";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }

            return $"{baseName} {Guid.NewGuid():N}";
        }

        private void Widget_GameBarDisplayModeChanged(XboxGameBarWidget sender, object args)
        {
            var ignored = Dispatcher?.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_closingForPinnedOnly)
                {
                    return;
                }

                try
                {
                    if (sender != null && sender.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly)
                    {
                        _closingForPinnedOnly = true;
                        App.BootstrapLog("[SettingsPage] Game Bar dismissed; returning to widget page.");
                        SettingsWindow.CloseCurrent();
                    }
                }
                catch (Exception ex)
                {
                    App.BootstrapLog("[SettingsPage] Failed to react to display mode change.", ex);
                }
            });
        }
    }
}
