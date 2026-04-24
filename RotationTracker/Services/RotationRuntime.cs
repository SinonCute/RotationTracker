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
        private const int FinalStrikeBurstClicks = 3;
        private const int FinalStrikeBurstWindowMs = 1500;
        private const int FinalStrikeHoldMs = 2000;

        private RotationDefinition _definition;
        private int _currentStepIndex;
        private int _finalStrikeClickCount;
        private long _finalStrikeLastClickTimestamp;
        private long _finalStrikeLeftDownTimestamp;
        private bool _finalStrikeLeftDown;
        private bool _leftButtonIsDown;

        public RotationDefinition Definition
        {
            get => _definition;
            set
            {
                if (_definition == value) return;
                _definition = value;
                _currentStepIndex = 0;
                ResetFinalStrikeState();
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
                ResetFinalStrikeState();
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
            ResetFinalStrikeState();
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
        public bool TryAdvance(InputKind kind, string key, long timestamp = 0)
        {
            var step = CurrentStep;
            if (step == null) return false;

            TrackMouseState(kind, key);

            if (step.Action == RotationAction.FinalStrike)
            {
                return TryAdvanceFinalStrike(kind, key, timestamp);
            }

            var expected = ActionInput.For(step.SlotIndex, step.Action);
            if (!expected.Matches(kind, key)) return false;

            Advance();
            return true;
        }

        public bool TryAdvanceFinalStrikeHold(long timestamp)
        {
            var step = CurrentStep;
            if (step == null || step.Action != RotationAction.FinalStrike) return false;
            if (!_finalStrikeLeftDown || _finalStrikeLeftDownTimestamp <= 0) return false;
            if ((NormalizeTimestamp(timestamp) - _finalStrikeLeftDownTimestamp) < FinalStrikeHoldMs) return false;

            Advance();
            return true;
        }

        private bool TryAdvanceFinalStrike(InputKind kind, string key, long timestamp)
        {
            if (!string.Equals(key, "LMB", System.StringComparison.OrdinalIgnoreCase)) return false;

            long now = NormalizeTimestamp(timestamp);
            if (kind == InputKind.MouseLeftUp)
            {
                _finalStrikeLeftDown = false;
                _finalStrikeLeftDownTimestamp = 0;
                return false;
            }

            if (kind != InputKind.MouseLeft)
            {
                return false;
            }

            _finalStrikeLeftDown = true;
            _finalStrikeLeftDownTimestamp = now;

            if (_finalStrikeLastClickTimestamp > 0
                && (now - _finalStrikeLastClickTimestamp) <= FinalStrikeBurstWindowMs)
            {
                _finalStrikeClickCount++;
            }
            else
            {
                _finalStrikeClickCount = 1;
            }

            _finalStrikeLastClickTimestamp = now;
            if (_finalStrikeClickCount < FinalStrikeBurstClicks)
            {
                return false;
            }

            Advance();
            return true;
        }

        private static long NormalizeTimestamp(long timestamp) =>
            timestamp > 0 ? timestamp : System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void ResetFinalStrikeState()
        {
            _finalStrikeClickCount = 0;
            _finalStrikeLastClickTimestamp = 0;
            _finalStrikeLeftDownTimestamp = 0;
            _finalStrikeLeftDown = false;

            if (CurrentStep?.Action == RotationAction.FinalStrike && _leftButtonIsDown)
            {
                _finalStrikeLeftDown = true;
                _finalStrikeLeftDownTimestamp = NormalizeTimestamp(0);
            }
        }

        private void TrackMouseState(InputKind kind, string key)
        {
            if (!string.Equals(key, "LMB", System.StringComparison.OrdinalIgnoreCase)) return;
            if (kind == InputKind.MouseLeft) _leftButtonIsDown = true;
            else if (kind == InputKind.MouseLeftUp) _leftButtonIsDown = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
