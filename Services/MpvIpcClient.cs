using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    public sealed class MpvEventArgs : EventArgs
    {
        public string EventName { get; private set; }
        public JObject Raw { get; private set; }

        public MpvEventArgs(string eventName, JObject raw)
        {
            EventName = eventName;
            Raw = raw;
        }
    }

    // Failure of an mpv IPC command. The message is always safe to show or log:
    // it never contains command arguments (which can include playback URLs).
    public sealed class MpvCommandException : Exception
    {
        public MpvCommandException(string message) : base(message) { }
    }

    // Owns mpv's JSON IPC protocol over an already-connected pipe stream:
    // - One background read loop runs for the lifetime of the connection,
    //   independent of any individual command.
    // - Lines with a "request_id" complete the matching pending command.
    // - Lines with an "event" are raised as MpvEventArgs and never touch the
    //   command-reply bookkeeping.
    //
    // This is the fix for the previous design, where the pipe reader only ran
    // while a command was in flight, so async events sent while idle (end-file,
    // track-list changes, pause toggled by mpv itself, etc) were silently lost
    // or misread as stale replies to the next unrelated command.
    public sealed class MpvIpcClient : IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending =
            new ConcurrentDictionary<int, TaskCompletionSource<JObject>>();

        private Stream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private CancellationTokenSource _cts;
        private Task _readLoopTask;
        private int _requestId;
        private bool _disposed;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public event EventHandler<MpvEventArgs> EventReceived;
        public event EventHandler Disconnected;

        public void Start(Stream stream)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvIpcClient));

            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _cts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string line = await _reader.ReadLineAsync().ConfigureAwait(false);

                    // Null means the pipe was closed from the other end.
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    HandleLine(line);
                }
            }
            catch
            {
                // Pipe faulted or was disposed out from under the read loop.
                // Fall through to the shared teardown below either way.
            }
            finally
            {
                FailAllPending();

                EventHandler disconnected = Disconnected;
                if (disconnected != null)
                    disconnected(this, EventArgs.Empty);
            }
        }

        private void HandleLine(string line)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(line);
            }
            catch
            {
                // Not valid JSON on this line; ignore rather than crash the reader.
                return;
            }

            JToken requestIdToken;
            if (obj.TryGetValue("request_id", out requestIdToken))
            {
                int id;
                if (int.TryParse(requestIdToken.ToString(), out id))
                {
                    TaskCompletionSource<JObject> tcs;
                    if (_pending.TryRemove(id, out tcs))
                        tcs.TrySetResult(obj);
                }

                return;
            }

            JToken eventToken;
            if (obj.TryGetValue("event", out eventToken))
            {
                EventHandler<MpvEventArgs> handler = EventReceived;
                if (handler != null)
                    handler(this, new MpvEventArgs(eventToken.Value<string>(), obj));
            }
        }

        // Sends a raw mpv command array, e.g. RunCommandAsync("seek", 10, "relative").
        public Task RunCommandAsync(params object[] args)
        {
            return SendCommandAsync(new JArray(args));
        }

        // Same as RunCommandAsync but with a caller-chosen timeout, for
        // shutdown paths that must not wait the full default 5 seconds.
        public Task RunCommandWithTimeoutAsync(int timeoutMs, params object[] args)
        {
            return SendCommandAsync(new JArray(args), timeoutMs);
        }

        public async Task<JToken> GetPropertyAsync(string property)
        {
            JObject reply = await SendCommandAsync(new JArray("get_property", property)).ConfigureAwait(false);
            JToken data;
            return reply.TryGetValue("data", out data) ? data : null;
        }

        public Task SetPropertyAsync(string property, object value)
        {
            return SendCommandAsync(new JArray("set_property", property, value));
        }

        public Task ObservePropertyAsync(int observeId, string property)
        {
            return SendCommandAsync(new JArray("observe_property", observeId, property));
        }

        // Commands are written one at a time. The write itself is async and the
        // lock is a SemaphoreSlim (not a monitor) so no thread is blocked, and
        // both acquiring it and the write itself are bounded by the command
        // timeout so a wedged pipe cannot stall every later command forever.
        //
        // Exceptions raised here deliberately carry only the mpv command NAME,
        // never the argument list: a "loadfile" command contains the playback
        // URL (which may embed credentials), and these messages can end up in
        // logs or UI.
        private async Task<JObject> SendCommandAsync(JArray command, int timeoutMs = 5000)
        {
            if (_disposed || _stream == null || _writer == null)
                throw new MpvCommandException("mpv IPC is not connected.");

            string commandName = DescribeCommand(command);
            int id = Interlocked.Increment(ref _requestId);
            TaskCompletionSource<JObject> tcs =
                new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            JObject payload = new JObject
            {
                ["command"] = command,
                ["request_id"] = id
            };

            string json = payload.ToString(Newtonsoft.Json.Formatting.None);

            bool lockTaken = false;
            try
            {
                lockTaken = await _writeLock.WaitAsync(timeoutMs).ConfigureAwait(false);
                if (!lockTaken)
                {
                    _pending.TryRemove(id, out tcs);
                    throw new MpvCommandException("mpv command timed out (" + commandName + ").");
                }

                StreamWriter writer = _writer;
                if (_disposed || writer == null)
                {
                    _pending.TryRemove(id, out tcs);
                    throw new MpvCommandException("mpv IPC is not connected.");
                }

                Task writeTask = writer.WriteLineAsync(json);
                Task done = await Task.WhenAny(writeTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (!ReferenceEquals(done, writeTask))
                {
                    _pending.TryRemove(id, out tcs);
                    // Do not release the lock while the write is still pending:
                    // a second writer would interleave bytes with it.
                    SwallowAndRelease(writeTask);
                    lockTaken = false;
                    throw new MpvCommandException("mpv command timed out (" + commandName + ").");
                }

                await writeTask.ConfigureAwait(false);
            }
            catch (MpvCommandException)
            {
                throw;
            }
            catch (Exception)
            {
                _pending.TryRemove(id, out tcs);
                throw new MpvCommandException("Failed to send mpv command (" + commandName + ").");
            }
            finally
            {
                if (lockTaken)
                    _writeLock.Release();
            }

            Task finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);

            if (!ReferenceEquals(finished, tcs.Task))
            {
                _pending.TryRemove(id, out tcs);
                throw new MpvCommandException("mpv command timed out (" + commandName + ").");
            }

            JObject reply;
            try
            {
                reply = await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                throw new MpvCommandException("mpv IPC connection closed.");
            }

            JToken errorToken;
            if (reply.TryGetValue("error", out errorToken))
            {
                string error = errorToken.Value<string>();
                if (!string.IsNullOrWhiteSpace(error) &&
                    !string.Equals(error, "success", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MpvCommandException("mpv command failed (" + commandName + "): " + error);
                }
            }

            return reply;
        }

        // Releases the write lock once a write that outlived its timeout finally
        // completes (or faults), so the pipe is never written concurrently.
        private void SwallowAndRelease(Task writeTask)
        {
            writeTask.ContinueWith(
                t => { var ignored = t.Exception; try { _writeLock.Release(); } catch { } },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        private static string DescribeCommand(JArray command)
        {
            if (command == null || command.Count == 0)
                return "unknown";

            string name = command[0] == null ? null : command[0].ToString();
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }

        private void FailAllPending()
        {
            foreach (var kvp in _pending)
                kvp.Value.TrySetException(new InvalidOperationException("mpv IPC connection closed."));

            _pending.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts?.Cancel(); } catch { }

            // _reader/_writer were opened with leaveOpen:true (deliberately -
            // they share one stream, so neither should close it independently),
            // which means disposing them, as this method previously did on its
            // own, never actually released the pipe handle. Disposing _stream
            // directly is what returns the named-pipe handle to the OS. It also
            // unblocks a pending ReadLineAsync() in the background read loop,
            // which is how that loop notices shutdown when no command is
            // in flight to wake it.
            try { _stream?.Dispose(); } catch { }

            FailAllPending();

            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }

            _reader = null;
            _writer = null;
            _stream = null;
        }
    }
}
