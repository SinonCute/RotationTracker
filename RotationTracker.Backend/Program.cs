using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GameBarInputBridge.Shared;
using Windows.Storage;

namespace RotationTracker.Backend
{
    internal static class Program
    {
        private const string LogFileName = "backend.log";
        private const string MutexName = "RotationTracker.Backend.SingleInstance";
        private const int BackendProbeTimeoutMs = 500;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int SW_HIDE = 0;

        private static Mutex _mutex;
        private static PipeServer _pipe;
        private static KeyboardPoller _poller;
        private static MouseHook _mouseHook;
        private static RawInputSink _rawInput;
        private static CancellationTokenSource _cts;
        private static readonly ConcurrentDictionary<string, int> LastSentTick = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int DuplicateSuppressMs = 80;
        private static readonly object MouseStateLock = new object();
        private static long _lastLeftDownTimestamp;

        [STAThread]
        private static void Main(string[] args)
        {
#if !DEBUG
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) ShowWindow(handle, SW_HIDE);
#endif

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("UnhandledException: " + e.ExceptionObject);

            var packageSid = ResolvePackageSid(args);
            if (string.IsNullOrEmpty(packageSid))
            {
                Log("No package SID provided; cannot build AppContainer pipe path. Exiting.");
                return;
            }

            _mutex = new Mutex(true, MutexName, out var firstInstance);
            if (!firstInstance && ExistingBackendAcceptsPipe(packageSid))
            {
                Log("Backend already running and accepting pipe connections. Exiting.");
                return;
            }

            if (!firstInstance)
            {
                Log("Backend mutex is held, but the pipe is unreachable. Starting recovery backend.");
            }

            Log($"Starting backend. PackageSid={packageSid} ProcessId={System.Diagnostics.Process.GetCurrentProcess().Id}");

            _cts = new CancellationTokenSource();
            _pipe = new PipeServer(packageSid, Log);
            _poller = new KeyboardPoller();

            _pipe.LineReceived += OnLineReceived;
            _pipe.Connected += (_, __) => Log("Widget connected to pipe.");
            _pipe.Disconnected += (_, __) =>
            {
                Log("Widget disconnected from pipe.");
                _poller.SetWatchedKeys(Array.Empty<string>());
            };

            int sentKeys = 0;
            _poller.PressCompleted += (_, e) =>
            {
                var dedupe = (e.longPress ? "long " : "short ") + e.key;
                if (!TryMarkSend(dedupe)) return;
                _pipe.Send(WrapInput(new InputEvent
                {
                    Type = InputType.Keyboard,
                    Key = e.key,
                    Action = e.longPress ? InputActions.LongPress : InputActions.ShortPress,
                    Timestamp = (long)GetTickCount64(),
                }));
                int s = Interlocked.Increment(ref sentKeys);
                if (s <= 5 || s % 25 == 0)
                {
                    Log($"Sent {(e.longPress ? "long" : "short")} {e.key} to pipe. pipeConnected={_pipe.IsConnected} sent={s}");
                }
            };
            _poller.KeyUp += (_, key) =>
            {
                _pipe.Send(WrapInput(new InputEvent
                {
                    Type = InputType.Keyboard,
                    Key = key,
                    Action = InputActions.KeyUp,
                    Timestamp = (long)GetTickCount64(),
                }));
            };
            if (_poller.Start())
            {
                Log("[KeyboardHook] Installed WH_KEYBOARD_LL hook.");
            }
            else
            {
                Log($"[KeyboardHook] Failed to install hook. win32_err={_poller.LastStartError}");
            }

            _mouseHook = new MouseHook(Log);
            int sentClicks = 0;
            _mouseHook.LeftButtonDown += (_, ev) =>
            {
                ProcessLeftButtonDown(ev.X, ev.Y, ref sentClicks);
            };
            _mouseHook.LeftButtonUp += (_, __) => ProcessLeftButtonUp();
            var mouseHookStarted = _mouseHook.Start();
            if (!mouseHookStarted)
            {
                Log("[MouseHook] Failed to install WH_MOUSE_LL hook.");
            }

            _rawInput = new RawInputSink(Log);
            _rawInput.KeyDown += (_, vk) => _poller.ProcessVirtualKey(vk, true);
            _rawInput.KeyUp += (_, vk) => _poller.ProcessVirtualKey(vk, false);
            _rawInput.LeftButtonDown += (_, __) =>
            {
                GetCursorPos(out var pt);
                ProcessLeftButtonDown(pt.X, pt.Y, ref sentClicks);
            };
            _rawInput.LeftButtonUp += (_, __) => ProcessLeftButtonUp();

            var pipeThread = new Thread(() => _pipe.Run(_cts.Token))
            {
                IsBackground = true,
                Name = "PipeServerLoop",
            };
            pipeThread.Start();

            Application.Run(new BackgroundContext(_cts));

            _mouseHook.Stop();
            _rawInput?.Dispose();
            _poller.Stop();
            pipeThread.Join(1000);
            Log("Backend exited.");
        }

        private static void OnLineReceived(object sender, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var msg = ClientPipeMessage.Deserialize(line);
            if (msg == null || string.IsNullOrEmpty(msg.Cmd))
            {
                Log($"Unknown client message: {line}");
                return;
            }

            var command = msg.Cmd.Trim().ToLowerInvariant();
            switch (command)
            {
                case ClientPipeMessageCmds.Watch:
                    var keys = (msg.Keys ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    _poller.SetWatchedKeys(keys);
                    Log($"Watching: {string.Join(",", keys)}");
                    _pipe.Send(new ServerPipeMessage { Kind = ServerPipeMessageKinds.Ready });
                    break;
                case ClientPipeMessageCmds.Stop:
                    _poller.SetWatchedKeys(Array.Empty<string>());
                    Log("Stopped watching keys.");
                    _pipe.Send(new ServerPipeMessage { Kind = ServerPipeMessageKinds.AckStop });
                    break;
                case ClientPipeMessageCmds.Ping:
                    _pipe.Send(new ServerPipeMessage { Kind = ServerPipeMessageKinds.Pong });
                    break;
                default:
                    Log($"Unknown command: {command}");
                    break;
            }
        }

        private static ServerPipeMessage WrapInput(InputEvent e) =>
            new ServerPipeMessage { Kind = ServerPipeMessageKinds.Input, Event = e };

        private static string ResolvePackageSid(string[] args)
        {
            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && args[0].StartsWith("S-1-"))
            {
                return args[0];
            }

            try
            {
                return ApplicationData.Current.LocalSettings.Values["PackageSid"] as string;
            }
            catch (Exception ex)
            {
                Log($"Failed to read PackageSid from LocalSettings: {ex.Message}");
                return null;
            }
        }

        private static bool ExistingBackendAcceptsPipe(string packageSid)
        {
            var pipeName = $"Sessions\\{System.Diagnostics.Process.GetCurrentProcess().SessionId}\\AppContainerNamedObjects\\{packageSid}\\RotationTrackerPipe";

            try
            {
                using (var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous))
                {
                    client.Connect(BackendProbeTimeoutMs);
                    return client.IsConnected;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine(message);
            try
            {
                var folder = ApplicationData.Current?.LocalFolder?.Path;
                if (string.IsNullOrEmpty(folder)) folder = Path.GetTempPath();
                var path = Path.Combine(folder, LogFileName);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} | {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static bool TryMarkSend(string token)
        {
            int now = Environment.TickCount;
            if (LastSentTick.TryGetValue(token, out var prev))
            {
                unchecked
                {
                    if ((now - prev) < DuplicateSuppressMs)
                    {
                        return false;
                    }
                }
            }

            LastSentTick[token] = now;
            return true;
        }

        private static void ProcessLeftButtonDown(int x, int y, ref int sentClicks)
        {
            long now = (long)GetTickCount64();
            bool emitShort = false;

            lock (MouseStateLock)
            {
                if ((now - _lastLeftDownTimestamp) < DuplicateSuppressMs)
                {
                    return;
                }

                _lastLeftDownTimestamp = now;
                emitShort = true;
            }

            if (emitShort && TryMarkSend("short LMB"))
            {
                _pipe.Send(WrapInput(new InputEvent
                {
                    Type = InputType.Mouse,
                    Key = "LMB",
                    Action = InputActions.MouseLeftDown,
                    X = x,
                    Y = y,
                    Timestamp = now,
                }));
                int s = Interlocked.Increment(ref sentClicks);
                if (s <= 5 || s % 25 == 0)
                {
                    Log($"Sent short LMB to pipe. pipeConnected={_pipe.IsConnected} sent={s}");
                }
            }
        }

        private static void ProcessLeftButtonUp()
        {
            long now = (long)GetTickCount64();
            if (!TryMarkSend("up LMB"))
            {
                return;
            }

            _pipe.Send(WrapInput(new InputEvent
            {
                Type = InputType.Mouse,
                Key = "LMB",
                Action = InputActions.MouseLeftUp,
                Timestamp = now,
            }));
        }

        private sealed class BackgroundContext : ApplicationContext
        {
            private readonly CancellationTokenSource _cts;

            public BackgroundContext(CancellationTokenSource cts)
            {
                _cts = cts ?? new CancellationTokenSource();
                Application.ApplicationExit += (_, __) => _cts.Cancel();
            }
        }
    }
}
