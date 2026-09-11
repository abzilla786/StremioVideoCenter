using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    /// <summary>
    /// Owns exactly one mpv.exe process and exactly one JSON IPC connection.
    /// A new MpvPlayer gets a new service, so playback sessions never share
    /// process/pipe state.
    /// </summary>
    public sealed class MpvProcessService : IDisposable
    {
        private readonly string _pipeName;
        private Process _process;
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Task _ipcReaderTask;
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>> _pendingCommands =
            new ConcurrentDictionary<int, TaskCompletionSource<IpcResponse>>();
        private int _nextRequestId;
        private bool _disposed;
        private bool _ipcStopping;

        public MpvProcessService()
        {
            _pipeName = "StremioVideoCenter_Mpv_" + Guid.NewGuid().ToString("N");
        }

        public bool IsRunning
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public int ProcessId
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited
                        ? _process.Id
                        : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        public async Task StartAsync(IntPtr hostHandle)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvProcessService));

            if (hostHandle == IntPtr.Zero)
                throw new ArgumentException("MPV host window handle cannot be zero.", nameof(hostHandle));

            if (IsRunning)
                return;

            string executablePath = FindMpvExecutable();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new FileNotFoundException(
                    "mpv.exe could not be found. Expected an 'mpv' folder containing mpv.exe.");
            }

            string workingDirectory = Path.GetDirectoryName(executablePath);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments =
                        "--wid=" + hostHandle.ToInt64() +
                        " --no-config" +
                        // Keep the renderer configuration identical to the build
                        // that successfully rendered the embedded first session.
                        " --idle=yes" +
                        " --keep-open=yes" +
                        " --keep-open-pause=yes" +
                        " --force-window=yes" +
                        " --no-osc" +
                        " --osd-level=0" +
                        " --input-terminal=no" +
                        " --input-default-bindings=no" +
                        " --input-builtin-bindings=no" +
                        " --input-vo-keyboard=no" +
                        " --input-cursor=no" +
                        " --input-builtin-dragging=no" +
                        " --input-builtin-drag-and-drop=no" +
                        " --log-file=\"" + Path.Combine(Path.GetTempPath(), "StremioVideoCenter_mpv.log") + "\"" +
                        " --input-ipc-server=\\\\.\\pipe\\" + _pipeName
                }
            };

            _process.EnableRaisingEvents = true;

            if (!_process.Start())
                throw new InvalidOperationException("Unable to start mpv.exe.");

            _pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await _pipe.ConnectAsync(5000).ConfigureAwait(false);
            }
            catch
            {
                DisposeProcessOnly();
                throw new InvalidOperationException(
                    "mpv started, but its JSON IPC pipe did not become available.");
            }

            _reader = new StreamReader(
                _pipe,
                new UTF8Encoding(false),
                false,
                4096,
                true);

            _writer = new StreamWriter(
                _pipe,
                new UTF8Encoding(false),
                4096,
                true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            // IMPORTANT: mpv sends asynchronous events as well as command
            // replies. The dedicated reader loop prevents an event from being
            // accidentally consumed as the response to a command.
            _ipcStopping = false;
            _ipcReaderTask = Task.Run(ReadIpcLoopAsync);

            await Task.Delay(150).ConfigureAwait(false);

            if (!IsRunning)
            {
                int exitCode = GetExitCode();
                DisposeProcessOnly();
                throw new InvalidOperationException(
                    "mpv started but exited during initialisation. Exit code: " +
                    exitCode +
                    ". See the MPV log in the Windows Temp folder: " +
                    Path.Combine(Path.GetTempPath(), "StremioVideoCenter_mpv.log"));
            }
        }

        public async Task PlayAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL cannot be empty.", nameof(url));

            await SendCommandAsync(
                "[\"loadfile\",\"" + JsonEscape(url) + "\",\"replace\"]")
                .ConfigureAwait(false);
        }

        public async Task PauseAsync()
        {
            await SendCommandAsync("[\"cycle\",\"pause\"]")
                .ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            if (!IsRunning || _pipe == null || !_pipe.IsConnected)
                return;

            await SendCommandAsync("[\"stop\"]")
                .ConfigureAwait(false);
        }

        public async Task SeekRelativeAsync(double seconds)
        {
            await SendCommandAsync(
                "[\"seek\"," + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"relative\"]")
                .ConfigureAwait(false);
        }

        public async Task SetVolumeAsync(double volume)
        {
            volume = Math.Max(0, Math.Min(100, volume));

            await SendCommandAsync(
                "[\"set_property\",\"volume\"," + volume.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]")
                .ConfigureAwait(false);
        }

        public async Task ToggleMuteAsync()
        {
            await SendCommandAsync("[\"cycle\",\"mute\"]")
                .ConfigureAwait(false);
        }

        private async Task SendCommandAsync(string commandArrayJson)
        {
            if (!IsRunning || _pipe == null || !_pipe.IsConnected || _writer == null)
                throw CreateNotRunningException();

            await _commandLock.WaitAsync().ConfigureAwait(false);

            int requestId = Interlocked.Increment(ref _nextRequestId);
            TaskCompletionSource<IpcResponse> completion =
                new TaskCompletionSource<IpcResponse>();

            _pendingCommands[requestId] = completion;

            try
            {
                string json =
                    "{\"command\":" +
                    commandArrayJson +
                    ",\"request_id\":" +
                    requestId +
                    "}";

                await _writer.WriteLineAsync(json).ConfigureAwait(false);

                Task completed = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(5000)).ConfigureAwait(false);

                if (completed != completion.Task)
                    throw new TimeoutException(
                        "Timed out waiting for mpv IPC response (request " +
                        requestId + ").");

                IpcResponse response = await completion.Task.ConfigureAwait(false);

                if (response == null)
                    throw new InvalidOperationException("mpv returned an empty IPC response.");

                if (!string.IsNullOrWhiteSpace(response.Error) &&
                    !string.Equals(response.Error, "success", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "mpv rejected the command: " + response.Error);
                }
            }
            finally
            {
                TaskCompletionSource<IpcResponse> ignored;
                _pendingCommands.TryRemove(requestId, out ignored);
                _commandLock.Release();
            }
        }

        private async Task ReadIpcLoopAsync()
        {
            try
            {
                while (!_ipcStopping && _reader != null && _pipe != null && _pipe.IsConnected)
                {
                    string line = await _reader.ReadLineAsync().ConfigureAwait(false);

                    if (line == null)
                        break;

                    IpcResponse response = DeserializeResponse(line);
                    if (response == null || !response.RequestId.HasValue)
                    {
                        // This is normally an asynchronous mpv event. It is
                        // intentionally not matched to any command.
                        continue;
                    }

                    TaskCompletionSource<IpcResponse> completion;
                    if (_pendingCommands.TryGetValue(
                            response.RequestId.Value,
                            out completion))
                    {
                        completion.TrySetResult(response);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                CompletePendingWithException(ex);
            }

            if (!_ipcStopping && IsRunning)
            {
                CompletePendingWithException(
                    new InvalidOperationException("mpv JSON IPC connection closed unexpectedly."));
            }
        }

        private static IpcResponse DeserializeResponse(string json)
        {
            try
            {
                var serializer =
                    new DataContractJsonSerializer(typeof(IpcResponse));

                using (var stream =
                       new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (IpcResponse)serializer.ReadObject(stream);
                }
            }
            catch
            {
                // Unknown/asynchronous messages should never break the IPC
                // reader. They can safely be ignored here.
                return null;
            }
        }

        private void CompletePendingWithException(Exception exception)
        {
            foreach (var pair in _pendingCommands)
            {
                pair.Value.TrySetException(exception);
            }
        }

        private InvalidOperationException CreateNotRunningException()
        {
            return new InvalidOperationException(
                "mpv is not running. Exit code: " +
                GetExitCode() +
                ". See the MPV log in the Windows Temp folder: " +
                Path.Combine(Path.GetTempPath(), "StremioVideoCenter_mpv.log"));
        }

        private int GetExitCode()
        {
            try
            {
                if (_process != null && _process.HasExited)
                    return _process.ExitCode;
            }
            catch { }

            return -1;
        }

        private static string FindMpvExecutable()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string currentDirectory = Environment.CurrentDirectory;

            string[] candidates =
            {
                Path.Combine(baseDirectory, "mpv", "mpv.exe"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "mpv", "mpv.exe")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "mpv", "mpv.exe")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "mpv", "mpv.exe")),
                Path.Combine(currentDirectory, "mpv", "mpv.exe"),
                Path.Combine(
                    Path.GetDirectoryName(typeof(MpvProcessService).Assembly.Location) ?? string.Empty,
                    "mpv",
                    "mpv.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
                return string.Empty;

            StringBuilder builder = new StringBuilder(value.Length + 16);

            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32)
                            builder.Append("\\u" + ((int)character).ToString("x4"));
                        else
                            builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        private void DisposeProcessOnly()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch { }

            try { _process?.Dispose(); } catch { }
            _process = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Stop accepting/processing new commands while shutdown is in
            // progress. Existing quit command is sent directly through the
            // normal request/response path so the IPC reader stays in control.
            try
            {
                if (IsRunning && _pipe != null && _pipe.IsConnected && _writer != null)
                {
                    SendCommandAsync("[\"quit\"]")
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch { }

            try
            {
                if (_process != null && !_process.HasExited)
                    _process.WaitForExit(2000);
            }
            catch { }

            _ipcStopping = true;

            // If mpv did not honour quit, force the process down before the
            // WPF HwndHost is destroyed. This prevents a stale native child
            // HWND surviving into the next playback session.
            if (IsRunning)
                DisposeProcessOnly();

            try { _pipe?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }

            _pipe = null;
            _reader = null;
            _writer = null;

            try
            {
                if (_ipcReaderTask != null)
                    _ipcReaderTask.Wait(500);
            }
            catch { }

            CompletePendingWithException(
                new ObjectDisposedException(nameof(MpvProcessService)));

            try { _commandLock.Dispose(); } catch { }
        }

        [DataContract]
        private sealed class IpcResponse
        {
            [DataMember(Name = "request_id")]
            public int? RequestId { get; set; }

            [DataMember(Name = "error")]
            public string Error { get; set; }
        }
    }
}
