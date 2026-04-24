using System.Text.Json;

namespace GameBarInputBridge.Shared
{
    /// <summary>Values for <see cref="InputEvent.Action"/> (camelCase on wire).</summary>
    public static class InputActions
    {
        public const string ShortPress = "shortPress";
        public const string LongPress = "longPress";
        public const string KeyUp = "keyUp";
        public const string KeyDown = "keyDown";
        public const string MouseLeftDown = "mouseLeftDown";
        public const string MouseLeftUp = "mouseLeftUp";
        public const string MouseLeftBurst = "mouseLeftBurst";
        public const string MouseLeftHold = "mouseLeftHold";
        public const string MouseRightDown = "mouseRightDown";
        public const string MouseMove = "mouseMove";
    }

    public enum InputType
    {
        Keyboard,
        Mouse,
    }

    /// <summary>
    /// Keyboard: Action ShortPress | LongPress | KeyUp | KeyDown.
    /// Mouse: Action MouseLeftDown | MouseRightDown | MouseMove | ...
    /// </summary>
    public sealed class InputEvent
    {
        public InputType Type { get; set; }
        public string Key { get; set; } = "";
        public string Action { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public long Timestamp { get; set; }

        public string Serialize() =>
            JsonSerializer.Serialize(this, BridgeJson.Options);

        public static InputEvent? Deserialize(string json) =>
            string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<InputEvent>(json, BridgeJson.Options);
    }

    public static class ServerPipeMessageKinds
    {
        public const string Input = "input";
        public const string Ready = "ready";
        public const string Pong = "pong";
        public const string AckStop = "ackStop";
    }

    /// <summary>One JSON line from server to widget.</summary>
    public sealed class ServerPipeMessage
    {
        public string Kind { get; set; } = "";
        public InputEvent? Event { get; set; }

        public string Serialize() =>
            JsonSerializer.Serialize(this, BridgeJson.Options);

        public static ServerPipeMessage? Deserialize(string json) =>
            string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ServerPipeMessage>(json, BridgeJson.Options);
    }

    public static class ClientPipeMessageCmds
    {
        public const string Watch = "watch";
        public const string Stop = "stop";
        public const string Ping = "ping";
    }

    /// <summary>One JSON line from widget to server.</summary>
    public sealed class ClientPipeMessage
    {
        public string Cmd { get; set; } = "";
        public string Keys { get; set; } = "";

        public string Serialize() =>
            JsonSerializer.Serialize(this, BridgeJson.Options);

        public static ClientPipeMessage? Deserialize(string json) =>
            string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ClientPipeMessage>(json, BridgeJson.Options);
    }
}
