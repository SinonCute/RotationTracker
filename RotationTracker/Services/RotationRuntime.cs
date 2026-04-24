using RotationTracker.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RotationTracker.Services
{
    /// <summary>
    /// Runtime state for a single rotation play-through.
    /// Advances forward either by a matching input event (from the backend)
    /// or through an explicit <see cref="Advance"/> call.
    /// </summary>
    public sealed class RotationRuntime : INotifyPropertyChanged
    {
        private RotationDefinition _definition;
        private int _currentStepIndex;

        public RotationDefinition Definition
        {
            get => _definition;
            set
            {
                if (_definition == value) return;
                _definition = value;
                _currentStepIndex = 0;
                OnPropertyChanged(nameof(Definition));
                OnPropertyChanged(nameof(CurrentStepIndex));
                OnPropertyChanged(nameof(CurrentStep));
                OnPropertyChanged(nameof(HasDefinition));
            }
        }

        public int CurrentStepIndex
        {
            get => _currentStepIndex;
            private set
            {
                if (_currentStepIndex == value) return;
                _currentStepIndex = value;
                OnPropertyChanged(nameof(CurrentStepIndex));
                OnPropertyChanged(nameof(CurrentStep));
            }
        }

        public RotationStep CurrentStep
        {
            get
            {
                if (_definition == null || _definition.Steps == null) return null;
                if (_currentStepIndex < 0 || _currentStepIndex >= _definition.Steps.Count) return null;
                return _definition.Steps[_currentStepIndex];
            }
        }

        public bool HasDefinition => _definition?.Steps != null && _definition.Steps.Count > 0;

        public bool LoopWhenComplete { get; set; } = true;

        public void Reset()
        {
            CurrentStepIndex = 0;
        }

        public void Advance()
        {
            if (!HasDefinition) return;

            int next = _currentStepIndex + 1;
            if (next >= _definition.Steps.Count)
            {
                next = LoopWhenComplete ? 0 : _definition.Steps.Count - 1;
            }
            CurrentStepIndex = next;
        }

        /// <summary>
        /// Advances the rotation if <paramref name="kind"/>/<paramref name="key"/>
        /// match the <see cref="ActionInput"/> expected by the current step.
        /// </summary>
        public bool TryAdvance(InputKind kind, string key)
        {
            var step = CurrentStep;
            if (step == null) return false;

            var expected = ActionInput.For(step.SlotIndex, step.Action);
            if (!expected.Matches(kind, key)) return false;

            Advance();
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
