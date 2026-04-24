using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Security.Authentication.Web;
using Windows.Storage;
using GameBarInputBridge.Shared;

namespace RotationTracker.Services
{
    public sealed class InputEventArgs : EventArgs
    {
        public InputKind Kind { get; }
        public string Key { get; }
        public long Timestamp { get; }

        public InputEventArgs(InputKind kind, string key, long timestamp)
        {
            Kind = kind;
            Key = key ?? "";
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Talks to the full-trust <c>RotationTracker.Backend</c> helper over a
    /// named pipe using JSON <see cref="ClientPipeMessage"/> /
    /// <see cref="ServerPipeMessage"/>.
    /// </summary>
    public sealed class BackendClient
    {
        private const string PipeName = @"LOCAL\RotationTrackerPipe";

        private static readonly Lazy<BackendClient> _instance =
            new Lazy<BackendClient>(() => new BackendClient());

        public static BackendClient Instance => _instance.Value;

        private CancellationTokenSource _cts;
        private Task _loopTask;
        private readonly object _sync = new object();
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _pendingWatch;

        public event EventHandler<InputEventArgs> InputDetected;
        public event EventHandler<string> KeyReleased;
        public event EventHandler ConnectedEvent;
        public event EventHandler DisconnectedEvent;

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _pipe != null && _pipe.IsConnected;
                }
            }
        }

        private BackendClient()
        {
        }

        public async Task StartAsync()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            App.BootstrapLog("[BackendClient] StartAsync called.");

            try
            {
                await LaunchBackendAsync();
                App.BootstrapLog("[BackendClient] LaunchBackendAsync completed.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BackendClient] LaunchBackend failed: {ex.Message}");
                App.BootstrapLog("[BackendClient] LaunchBackend failed.", ex);
            }

            _loopTask = Task.Run(() => ConnectLoop(_cts.Token));
        }

        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null) return;

            try { cts.Cancel(); } catch { }
            TeardownPipe();
        }

        public void SetWatchedKeys(IEnumerable<string> keys)
        {
            var joined = string.Join(",", keys ?? Array.Empty<string>());
            _pendingWatch = joined;
            App.BootstrapLog($"[BackendClient] SetWatchedKeys: {joined}");
            SendClient(new ClientPipeMessage { Cmd = ClientPipeMessageCmds.Watch, Keys = joined });
        }

        private void SendClient(ClientPipeMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.Cmd)) return;

            lock (_sync)
            {
                if (_pipe == null || !_pipe.IsConnected || _writer == null) return;
                try
                {
                    var line = message.Serialize();
                    _writer.WriteLine(line);
                    _writer.Flush();
                    if (string.Equals(message.Cmd, ClientPipeMessageCmds.Watch, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(message.Cmd, ClientPipeMessageCmds.Ping, StringComparison.OrdinalIgnoreCase))
                    {
                        App.BootstrapLog($"[BackendClient] > {line}");
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BackendClient] Send failed: {ex.Message}");
                    App.BootstrapLog("[BackendClient] Send failed.", ex);
                }
            }
        }

        private static async Task LaunchBackendAsync()
        {
            string sid = null;
            try
            {
                sid = WebAuthenticationBroker.GetCurrentApplicationCallbackUri().Host.ToUpperInvariant();
                ApplicationData.Current.LocalSettings.Values["PackageSid"] = sid;
                App.BootstrapLog($"[BackendClient] Stored PackageSid={sid}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[BackendClient] PackageSid resolve failed: {ex.Message}");
                App.BootstrapLog("[BackendClient] PackageSid resolve failed.", ex);
            }

            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
        }

        private async Task ConnectLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = new NamedPipeClientStream(
                        ".",
                        PipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);

                    await client.ConnectAsync(2000, ct).ConfigureAwait(false);

                    lock (_sync)
                    {
                        _pipe = client;
                        _reader = new StreamReader(_pipe);
                        _writer = new StreamWriter(_pipe) { AutoFlush = true };
                    }

                    Trace.WriteLine("[BackendClient] Connected to backend pipe.");
                    App.BootstrapLog("[BackendClient] Connected to backend pipe.");
                    try { ConnectedEvent?.Invoke(this, EventArgs.Empty); } catch { }

                    if (!string.IsNullOrEmpty(_pendingWatch))
                    {
                        SendClient(new ClientPipeMessage
                        {
                            Cmd = ClientPipeMessageCmds.Watch,
                            Keys = _pendingWatch,
                        });
                    }

                    await PumpLinesAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BackendClient] Connect/pump failed: {ex.Message}");
                    App.BootstrapLog("[BackendClient] Connect/pump failed.", ex);
                }
                finally
                {
                    TeardownPipe();
                    try { DisconnectedEvent?.Invoke(this, EventArgs.Empty); } catch { }
                }

                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task PumpLinesAsync(CancellationToken ct)
        {
            StreamReader reader;
            lock (_sync)
            {
                reader = _reader;
            }

            while (!ct.IsCancellationRequested && reader != null)
            {
                string line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[BackendClient] Read failed: {ex.Message}");
                    break;
                }

                if (line == null) break;
                HandleLine(line);
            }
        }

        private void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var msg = ServerPipeMessage.Deserialize(line);
            if (msg == null || string.IsNullOrEmpty(msg.Kind))
            {
                Trace.WriteLine($"[BackendClient] Unknown line: {line}");
                return;
            }

            var kind = msg.Kind.Trim();
            if (string.Equals(kind, ServerPipeMessageKinds.Input, StringComparison.OrdinalIgnoreCase))
            {
                if (msg.Event != null) HandleInputEvent(msg.Event);
                return;
            }

            if (string.Equals(kind, ServerPipeMessageKinds.Ready, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, ServerPipeMessageKinds.Pong, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, ServerPipeMessageKinds.AckStop, StringComparison.OrdinalIgnoreCase))
            {
                Trace.WriteLine($"[BackendClient] < {line}");
                return;
            }

            Trace.WriteLine($"[BackendClient] Unknown kind: {kind}");
        }

        private void HandleInputEvent(InputEvent e)
        {
            if (e == null) return;
            var action = e.Action?.Trim() ?? "";

            if (string.Equals(action, InputActions.ShortPress, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(e.Key))
                    Raise(InputKind.ShortPress, e.Key);
                return;
            }

            if (string.Equals(action, InputActions.LongPress, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(e.Key))
                    Raise(InputKind.LongPress, e.Key);
                return;
            }

            if (string.Equals(action, InputActions.KeyUp, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(e.Key))
                    try { KeyReleased?.Invoke(this, e.Key); } catch { }
                return;
            }

            if (string.Equals(action, InputActions.MouseLeftDown, StringComparison.OrdinalIgnoreCase))
            {
                Raise(InputKind.MouseLeft, "LMB");
                return;
            }

            if (string.Equals(action, InputActions.MouseLeftUp, StringComparison.OrdinalIgnoreCase))
            {
                Raise(InputKind.MouseLeftUp, "LMB");
                return;
            }

            // Older backends may still emit derived mouse gestures. The current
            // runtime computes Final Strike from raw LMB events instead.
            if (string.Equals(action, InputActions.MouseLeftBurst, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(action, InputActions.MouseLeftHold, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        private void Raise(InputKind kind, string key)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try { InputDetected?.Invoke(this, new InputEventArgs(kind, key, timestamp)); } catch { }
        }

        private void TeardownPipe()
        {
            lock (_sync)
            {
                try { _reader?.Dispose(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _reader = null;
                _writer = null;
                _pipe = null;
            }
        }
    }
}
