using RotationTracker.Models;
using System;

namespace RotationTracker.Services
{
    public enum InputKind
    {
        ShortPress = 0,
        LongPress = 1,
        MouseLeft = 2,
        MouseLeftBurst = 3,
        MouseLeftHold = 4,
        MouseLeftUp = 5,
    }

    public readonly struct ActionInput : IEquatable<ActionInput>
    {
        public InputKind Kind { get; }
        public string Key { get; }

        public ActionInput(InputKind kind, string key)
        {
            Kind = kind;
            Key = key ?? "";
        }

        public static ActionInput For(int slotIndex, RotationAction action)
        {
            int slot = slotIndex < 0 ? 0 : (slotIndex > 3 ? 3 : slotIndex);
            switch (action)
            {
                case RotationAction.Skill:
                    return new ActionInput(InputKind.ShortPress, (slot + 1).ToString());
                case RotationAction.Ultimate:
                    return new ActionInput(InputKind.LongPress, (slot + 1).ToString());
                case RotationAction.Combo:
                    return new ActionInput(InputKind.ShortPress, "E");
                case RotationAction.Normal:
                    return new ActionInput(InputKind.MouseLeft, "LMB");
                case RotationAction.FinalStrike:
                    return new ActionInput(InputKind.MouseLeftBurst, "Hold LMB");
                default:
                    return default;
            }
        }

        public bool Matches(InputKind kind, string key)
        {
            if (Kind == InputKind.MouseLeftBurst)
            {
                return kind == InputKind.MouseLeftBurst || kind == InputKind.MouseLeftHold;
            }

            if (Kind != kind) return false;
            if (Kind == InputKind.MouseLeft || Kind == InputKind.MouseLeftHold) return true;
            if (string.IsNullOrEmpty(Key)) return false;
            return string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(ActionInput other) => Kind == other.Kind &&
            string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is ActionInput other && Equals(other);

        public override int GetHashCode() =>
            ((int)Kind * 397) ^ (Key?.ToUpperInvariant().GetHashCode() ?? 0);

        public string ToHint()
        {
            if (Kind == InputKind.MouseLeft) return "LMB";
            if (Kind == InputKind.MouseLeftBurst) return "LMBx3/Hold 2s";
            if (Kind == InputKind.MouseLeftHold) return "Hold LMB 2s";
            return Key;
        }
    }
}
