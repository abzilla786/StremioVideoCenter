using Newtonsoft.Json.Linq;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenterControls.Player
{
    // Coordinates one mpv playback session: process + IPC + native host
    // lifecycle, and keeps PlaybackState in sync via mpv property observation
    // and events. Exposes only high-level playback intents to the UI layer
    // (MpvPlayer.xaml.cs) - the UI layer never touches MpvProcessService or
    // MpvIpcClient directly, and never parses mpv JSON itself.
    public sealed class PlayerSession : IDisposable
    {
        private const int ObserveIdPause = 1;
        private const int ObserveIdMute = 2;
        private const int ObserveIdVolume = 3;
        private const int ObserveIdTrackList = 4;
        private const int ObserveIdAudioId = 5;
        private const int ObserveIdSubId = 6;
        private const int ObserveIdEofReached = 7;

        private readonly MpvHost _host;
        private MpvProcessService _process;
        private MpvIpcClient _ipc;
        private bool _disposed;
        private bool _started;

        // Independent guard against PlayerSession.StartAsync being entered
        // concurrently, on top of (not instead of) MpvPlayer's own startup
        // gate - RecoverAndRetryPlayAsync calls StartAsync directly and
        // bypasses that outer gate entirely, so this is the only thing that
        // actually protects against that specific path. 0 = free, 1 = in
        // progress.
        private int _startInProgress;

        public PlaybackState State { get; private set; }

        public event EventHandler StateChanged;
        public event EventHandler TracksChanged;
        public event EventHandler EndOfFileReached;

        // Raised (on the IPC thread) only for end-file reason=error with a
        // non-empty file_error. State.EndFileError carries the message.
        public event EventHandler PlaybackFailed;
        public event EventHandler Disconnected;

        public PlayerSession(MpvHost host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            _host = host;
            State = new PlaybackState();
        }

        public bool IsRunning
        {
            get { return _started && _process != null && _process.IsRunning; }
        }

        public async Task StartAsync(IntPtr fallbackHostHandle)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PlayerSession));

            if (_started && IsRunning)
                return;

            if (Interlocked.CompareExchange(ref _startInProgress, 1, 0) != 0)
            {
                StartupTrace.Log("PlayerSession.StartAsync: REJECTED - already in progress on another call. " +
                    "This should never happen if callers gate correctly (MpvPlayer's startup semaphore, " +
                    "or RecoverAndRetryPlayAsync calling in without going through it).");
                throw new InvalidOperationException("PlayerSession.StartAsync is already in progress.");
            }

            try
            {
                StartupTrace.Log("PlayerSession.StartAsync: begin");

                TeardownMpvOnly();

                IntPtr hostHandle = _host.WindowHandle != IntPtr.Zero ? _host.WindowHandle : fallbackHostHandle;
                if (hostHandle == IntPtr.Zero)
                    throw new InvalidOperationException("MPV video host window could not be created.");

                _process = new MpvProcessService();
                NamedPipeClientStream pipe = await _process.StartAsync(hostHandle).ConfigureAwait(true);

                if (!_process.IsRunning)
                    throw new InvalidOperationException("mpv stopped immediately after startup.");

                _ipc = new MpvIpcClient();
                _ipc.EventReceived += Ipc_EventReceived;
                _ipc.Disconnected += Ipc_Disconnected;
                _ipc.Start(pipe);

                await _host.WaitForAndAttachMpvWindowAsync(_process.ProcessId, 3000).ConfigureAwait(true);

                // Fire-and-forget: property observation is explicitly best-effort
                // (see ObservePropertiesAsync's own catch) and must never gate
                // session startup. Previously this was awaited inline here, so a
                // single stuck observe_property reply could silently stall
                // StartAsync() - and therefore PlayAsync()/loadfile - for up to
                // the full 5-second per-command timeout, with nothing visible
                // happening on screen in the meantime.
                _ = ObservePropertiesAsync();

                _started = true;
                _host.SynchronizeNativeWindow();

                StartupTrace.Log("PlayerSession.StartAsync: completed successfully");
            }
            finally
            {
                Interlocked.Exchange(ref _startInProgress, 0);
            }
        }

        private async Task ObservePropertiesAsync()
        {
            try
            {
                await _ipc.ObservePropertyAsync(ObserveIdPause, "pause").ConfigureAwait(true);
                await _ipc.ObservePropertyAsync(ObserveIdMute, "mute").ConfigureAwait(true);
                await _ipc.ObservePropertyAsync(ObserveIdVolume, "volume").ConfigureAwait(true);
                await _ipc.ObservePropertyAsync(ObserveIdTrackList, "track-list").ConfigureAwait(true);
                await _ipc.ObservePropertyAsync(ObserveIdAudioId, "aid").ConfigureAwait(true);
                await _ipc.ObservePropertyAsync(ObserveIdSubId, "sid").ConfigureAwait(true);

                // mpv runs with --keep-open=yes, which suppresses end-file at
                // a normal EOF (the player just pauses on the last frame), so
                // eof-reached is how a genuine normal end is observed.
                await _ipc.ObservePropertyAsync(ObserveIdEofReached, "eof-reached").ConfigureAwait(true);
            }
            catch
            {
                // Non-fatal: pause/mute/volume/track state will simply stay at
                // their last-known values instead of updating live. Position/
                // duration still work since those are polled, not observed.
            }
        }

        public async Task PlayAsync(string url)
        {
            if (_disposed) return;

            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL cannot be empty.", nameof(url));

            if (!IsRunning)
                throw new InvalidOperationException("PlayerSession must be started before playing.");

            try
            {
                await _ipc.RunCommandAsync("loadfile", url, "replace").ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                await RecoverAndRetryPlayAsync(url).ConfigureAwait(true);
                return;
            }
            catch (InvalidOperationException)
            {
                await RecoverAndRetryPlayAsync(url).ConfigureAwait(true);
                return;
            }

            await _host.WaitForAndAttachMpvWindowAsync(_process.ProcessId, 3000).ConfigureAwait(true);
            State.ReachedEndOfFile = false;
            State.EndFileReason = null;
            State.EndFileError = null;
            RaiseStateChanged();
        }

        // If mpv died between sessions (crash, killed externally, IPC faulted),
        // rebuild the process once and retry the play command. This never
        // touches --wid/renderer launch configuration.
        private async Task RecoverAndRetryPlayAsync(string url)
        {
            IntPtr hostHandle = _host.WindowHandle;
            TeardownMpvOnly();
            await StartAsync(hostHandle).ConfigureAwait(true);
            await _ipc.RunCommandAsync("loadfile", url, "replace").ConfigureAwait(true);
            await _host.WaitForAndAttachMpvWindowAsync(_process.ProcessId, 3000).ConfigureAwait(true);
            State.ReachedEndOfFile = false;
            State.EndFileReason = null;
            State.EndFileError = null;
            RaiseStateChanged();
        }

        public async Task TogglePauseAsync()
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("cycle", "pause").ConfigureAwait(true);
        }

        public async Task SeekRelativeAsync(double seconds)
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("seek", seconds, "relative").ConfigureAwait(true);
        }

        public async Task SeekAbsoluteAsync(double seconds)
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("seek", Math.Max(0, seconds), "absolute").ConfigureAwait(true);
        }

        public async Task SetVolumeAsync(double volume)
        {
            if (!IsRunning) return;
            volume = Math.Max(0, Math.Min(100, volume));
            await _ipc.SetPropertyAsync("volume", volume).ConfigureAwait(true);
        }

        public async Task ToggleMuteAsync()
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("cycle", "mute").ConfigureAwait(true);
        }

        public async Task SelectAudioTrackAsync(int trackId)
        {
            if (!IsRunning) return;
            await _ipc.SetPropertyAsync("aid", trackId).ConfigureAwait(true);
        }

        // subtitleTrackId == null means "subtitles off".
        public async Task SelectSubtitleTrackAsync(int? subtitleTrackId)
        {
            if (!IsRunning) return;
            object value = subtitleTrackId.HasValue ? (object)subtitleTrackId.Value : "no";
            await _ipc.SetPropertyAsync("sid", value).ConfigureAwait(true);
        }

        // Called by the UI's progress timer. Position/duration are polled
        // rather than observed - sub-second push updates aren't needed for a
        // seek bar, and this keeps the observed-property set small.
        public async Task PollPositionAsync()
        {
            if (!IsRunning) return;

            try
            {
                JToken position = await _ipc.GetPropertyAsync("time-pos").ConfigureAwait(true);
                JToken duration = await _ipc.GetPropertyAsync("duration").ConfigureAwait(true);

                double? durationValue = ToNullableDouble(duration);
                if (durationValue.HasValue && durationValue.Value > 0)
                    State.DurationSeconds = durationValue.Value;

                double? positionValue = ToNullableDouble(position);
                if (positionValue.HasValue)
                    State.PositionSeconds = Math.Max(0, positionValue.Value);

                RaiseStateChanged();
            }
            catch
            {
                // mpv may be between EOF, restart, or shutdown; skip this tick.
            }
        }

        private void Ipc_EventReceived(object sender, MpvEventArgs e)
        {
            switch (e.EventName)
            {
                case "property-change":
                    HandlePropertyChange(e.Raw);
                    break;

                case "end-file":
                    HandleEndFile(e.Raw);
                    break;
            }
        }

        // Reason-aware end-file handling. Only reason=eof counts as a
        // normal end; error/stop/quit/redirect never set ReachedEndOfFile.
        // A genuine failure (reason=error WITH a file_error) additionally
        // raises PlaybackFailed. Nothing here inspects the URL, size,
        // duration or addon.
        private void HandleEndFile(JObject raw)
        {
            string reason;
            string fileError;
            ParseEndFile(raw, out reason, out fileError);

            // Diagnostic: reason/file_error only, never the raw event
            // (which can carry the playback URL).
            StremioVideoCenterLogger.Info(
                "MPV end-file: reason=" + (string.IsNullOrEmpty(reason) ? "(null)" : reason) +
                ", file_error=" + (string.IsNullOrEmpty(fileError) ? "(null)" : fileError));

            State.EndFileReason = string.IsNullOrEmpty(reason) ? null : reason;
            State.EndFileError = string.IsNullOrEmpty(fileError) ? null : fileError;

            bool isEof = string.Equals(reason, "eof", StringComparison.OrdinalIgnoreCase);
            bool isFailure =
                string.Equals(reason, "error", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(fileError);

            if (isEof)
                State.ReachedEndOfFile = true;

            RaiseStateChanged();

            if (isEof)
            {
                EventHandler eofHandler = EndOfFileReached;
                if (eofHandler != null)
                    eofHandler(this, EventArgs.Empty);
            }

            if (isFailure)
            {
                EventHandler failedHandler = PlaybackFailed;
                if (failedHandler != null)
                    failedHandler(this, EventArgs.Empty);
            }
        }

        private static void ParseEndFile(JObject raw, out string reason, out string fileError)
        {
            reason = null;
            fileError = null;

            try
            {
                JToken reasonToken = raw != null ? raw["reason"] : null;
                JToken fileErrorToken = raw != null ? raw["file_error"] : null;

                reason = reasonToken != null && reasonToken.Type == JTokenType.String
                    ? reasonToken.Value<string>()
                    : null;

                fileError = fileErrorToken != null && fileErrorToken.Type == JTokenType.String
                    ? fileErrorToken.Value<string>()
                    : null;
            }
            catch
            {
                // Diagnostic only - never let logging affect playback.
            }

        }

        private void HandlePropertyChange(JObject raw)
        {
            string name = raw.Value<string>("name");
            JToken data = raw["data"];
            bool tracksUpdated = false;

            switch (name)
            {
                case "pause":
                    if (data != null && data.Type == JTokenType.Boolean)
                        State.IsPlaying = !data.Value<bool>();
                    break;

                case "mute":
                    if (data != null && data.Type == JTokenType.Boolean)
                        State.IsMuted = data.Value<bool>();
                    break;

                case "eof-reached":
                    // true = playback genuinely reached its end (kept open);
                    // false/unavailable = not at the end.
                    State.ReachedEndOfFile =
                        data != null &&
                        data.Type == JTokenType.Boolean &&
                        data.Value<bool>();
                    break;

                case "volume":
                    double? volume = ToNullableDouble(data);
                    if (volume.HasValue)
                        State.Volume = volume.Value;
                    break;

                case "aid":
                    State.SelectedAudioTrackId = ToNullableInt(data);
                    break;

                case "sid":
                    State.SelectedSubtitleTrackId = ToNullableInt(data);
                    break;

                case "track-list":
                    if (data != null && data.Type == JTokenType.Array)
                    {
                        UpdateTrackLists((JArray)data);
                        tracksUpdated = true;
                    }
                    break;
            }

            RaiseStateChanged();

            if (tracksUpdated)
            {
                EventHandler tracksHandler = TracksChanged;
                if (tracksHandler != null)
                    tracksHandler(this, EventArgs.Empty);
            }
        }

        private void UpdateTrackLists(JArray trackList)
        {
            List<MpvTrackInfo> audio = new List<MpvTrackInfo>();
            List<MpvTrackInfo> subtitles = new List<MpvTrackInfo>();

            foreach (JToken token in trackList)
            {
                JObject obj = token as JObject;
                if (obj == null) continue;

                MpvTrackInfo track = MpvTrackInfo.FromJson(obj);
                if (track == null) continue;

                if (string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase))
                    audio.Add(track);
                else if (string.Equals(track.Type, "sub", StringComparison.OrdinalIgnoreCase))
                    subtitles.Add(track);
            }

            State.AudioTracks = audio;
            State.SubtitleTracks = subtitles;
        }

        private static double? ToNullableDouble(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<double>();
            return null;
        }

        private static int? ToNullableInt(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<int>();
            return null;
        }

        private void Ipc_Disconnected(object sender, EventArgs e)
        {
            EventHandler handler = Disconnected;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        // Tears down the mpv process/IPC only, leaving the native host control
        // itself intact so it can be reused by the next StartAsync call.
        private void TeardownMpvOnly()
        {
            if (_ipc != null)
            {
                _ipc.EventReceived -= Ipc_EventReceived;
                _ipc.Disconnected -= Ipc_Disconnected;
                try { _ipc.Dispose(); } catch { }
                _ipc = null;
            }

            if (_process != null)
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }

            _started = false;
        }

        // Full graceful shutdown: ask mpv to quit cleanly, give it a brief
        // window to exit, then tear down regardless.
        public async Task StopAsync()
        {
            if (_disposed) return;

            if (_ipc != null && _process != null && _process.IsRunning)
            {
                try { await _ipc.RunCommandAsync("quit").ConfigureAwait(false); }
                catch { }

                try { await Task.Delay(300).ConfigureAwait(false); }
                catch { }
            }

            TeardownMpvOnly();
        }

        public void Dispose()
        {
            if (_disposed) return;

            // StopAsync() must run while _disposed is still false: StopAsync()
            // uses _disposed as its own guard against being invoked after
            // disposal, so setting the flag first (the previous bug) made it
            // return immediately - the "quit" command was never sent and
            // TeardownMpvOnly() never ran, leaking the mpv process, the IPC
            // client, and the named pipe it owns.
            try { StopAsync().GetAwaiter().GetResult(); }
            catch { }

            _disposed = true;
        }
    }
}
