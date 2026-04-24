using System.Collections.ObjectModel;

namespace RotationTracker.Models
{
    public sealed class RotationSettings
    {
        public ObservableCollection<RotationDefinition> Rotations { get; set; }
            = new ObservableCollection<RotationDefinition>();

        public string ActiveRotationId { get; set; } = "";

        public double PinnedOpacity { get; set; } = 0.85;

        public bool AutoAdvanceOnKey { get; set; } = true;

        public bool LoopWhenComplete { get; set; } = true;
    }
}
