using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using GameBarInputBridge.Shared;

namespace RotationTracker.Backend
{
    /// <summary>
    /// Single-client line protocol named-pipe server reachable from the widget's
    /// AppContainer. Lines are simple text terminated by LF.
    ///
    /// client -> server:  JSON <see cref="ClientPipeMessage"/> (watch / stop / ping)
    /// server -> client:  JSON <see cref="ServerPipeMessage"/> (input + acks)
    /// </summary>
    internal sealed class PipeServer
    {
        private readonly string _pipeName;
        private readonly string _packageSid;
        private readonly object _writeLock = new object();
        private readonly Action<string> _log;

        private NamedPipeServerStream _server;
        private StreamReader _reader;
        private StreamWriter _writer;

        public event EventHandler Connected;
        public event EventHandler Disconnected;
        public event EventHandler<string> LineReceived;

        public bool IsConnected => _server != null && _server.IsConnected;

        public PipeServer(string packageSid, Action<string> log = null)
        {
            _packageSid = packageSid ?? throw new ArgumentNullException(nameof(packageSid));
            _log = log;

            // AppContainer-reachable pipe name; the widget connects using \\.\pipe\LOCAL\RotationTrackerPipe
            // because UWP resolves LOCAL\... to Sessions\<id>\AppContainerNamedObjects\<sid>\...
            _pipeName = $"Sessions\\{Process.GetCurrentProcess().SessionId}\\AppContainerNamedObjects\\{_packageSid}\\RotationTrackerPipe";
            Log($"[PipeServer] Pipe name: {_pipeName}");
        }

        public void Run(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    AcceptClient(cancellationToken);
                    PumpMessages(cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"[PipeServer] Loop error: {ex.Message}");
                    Thread.Sleep(500);
                }
                finally
                {
                    TeardownCurrent();
                }
            }
        }

        public void Send(ServerPipeMessage message)
        {
            if (message == null) return;
            SendRawLine(message.Serialize());
        }

        private void SendRawLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (!IsConnected) return;

            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                Log($"[PipeServer] Send failed: {ex.Message}");
            }
        }

        private void AcceptClient(CancellationToken cancellationToken)
        {
            _server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                128,
                128,
                BuildPipeSecurity(_packageSid));

            _reader = new StreamReader(_server);
            _writer = new StreamWriter(_server);

            Log("[PipeServer] Waiting for client...");
            var wait = _server.BeginWaitForConnection(null, null);

            while (!wait.IsCompleted)
            {
                if (cancellationToken.WaitHandle.WaitOne(200))
                {
                    try { _server.Dispose(); } catch { }
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            _server.EndWaitForConnection(wait);
            Log("[PipeServer] Client connected.");
            Connected?.Invoke(this, EventArgs.Empty);
        }

        private void PumpMessages(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _server.IsConnected)
            {
                string line;
                try
                {
                    line = _reader.ReadLine();
                }
                catch (Exception ex)
                {
                    Log($"[PipeServer] Read failed: {ex.Message}");
                    break;
                }

                if (line == null) break;

                Log($"[PipeServer] < {line}");
                LineReceived?.Invoke(this, line);
            }
        }

        private void TeardownCurrent()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }
            _reader = null;
            _writer = null;
            _server = null;

            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private static PipeSecurity BuildPipeSecurity(string packageSid)
        {
            var ps = new PipeSecurity();
            // Drop inherited ACEs so only the AppContainer SID and this user can reach the pipe.
            ps.SetAccessRuleProtection(true, false);

            var clientRule = new PipeAccessRule(
                new SecurityIdentifier(packageSid),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow);
            var ownerRule = new PipeAccessRule(
                WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow);
            ps.AddAccessRule(clientRule);
            ps.AddAccessRule(ownerRule);
            return ps;
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
                return;
            }

            Console.WriteLine(message);
        }
    }
}
