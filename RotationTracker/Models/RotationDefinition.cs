using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RotationTracker.Models
{
    public sealed class RotationDefinition : INotifyPropertyChanged
    {
        public const int SlotCount = 4;

        private string _id = Guid.NewGuid().ToString("N");
        private string _name = "New Team";
        private ObservableCollection<string> _operatorSlots = CreateEmptySlots();
        private ObservableCollection<RotationStep> _steps = new ObservableCollection<RotationStep>();

        public string Id
        {
            get => _id;
            set
            {
                if (_id == value) return;
                _id = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;
                OnPropertyChanged();
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Always exactly 4 entries; each is an operator id from the catalog
        /// or an empty string when that slot is unassigned.
        /// </summary>
        public ObservableCollection<string> OperatorSlots
        {
            get => _operatorSlots;
            set
            {
                _operatorSlots = NormalizeSlots(value);
                OnPropertyChanged();
            }
        }

        public ObservableCollection<RotationStep> Steps
        {
            get => _steps;
            set
            {
                _steps = value ?? new ObservableCollection<RotationStep>();
                OnPropertyChanged();
            }
        }

        public static ObservableCollection<string> CreateEmptySlots()
        {
            var slots = new ObservableCollection<string>();
            for (int i = 0; i < SlotCount; i++) slots.Add("");
            return slots;
        }

        public static ObservableCollection<string> NormalizeSlots(ObservableCollection<string> input)
        {
            var result = new ObservableCollection<string>();
            if (input != null)
            {
                foreach (var entry in input)
                {
                    if (result.Count >= SlotCount) break;
                    result.Add(entry ?? "");
                }
            }
            while (result.Count < SlotCount) result.Add("");
            return result;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
