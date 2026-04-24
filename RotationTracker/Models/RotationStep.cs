using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RotationTracker.Models
{
    public enum RotationAction
    {
        Skill = 0,
        Combo = 1,
        Normal = 2,
        Ultimate = 3,
        FinalStrike = 4,
    }

    public sealed class RotationStep : INotifyPropertyChanged
    {
        private int _slotIndex;
        private RotationAction _action = RotationAction.Skill;
        private string _labelOverride = "";

        /// <summary>
        /// 0-3, indexing into the rotation's <c>OperatorSlots</c>.
        /// </summary>
        public int SlotIndex
        {
            get => _slotIndex;
            set
            {
                int clamped = value < 0 ? 0 : (value > 3 ? 3 : value);
                if (_slotIndex == clamped) return;
                _slotIndex = clamped;
                OnPropertyChanged();
            }
        }

        public RotationAction Action
        {
            get => _action;
            set
            {
                if (_action == value) return;
                _action = value;
                OnPropertyChanged();
            }
        }

        public string LabelOverride
        {
            get => _labelOverride;
            set
            {
                if (_labelOverride == value) return;
                _labelOverride = value ?? "";
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
