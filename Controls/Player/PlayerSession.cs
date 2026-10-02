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
        private const int ObserveIdSubDelay = 8;

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

        // Guards PollPositionAsync against overlapping calls (a slow mpv
        // reply must not let ticks pile up). 0 = free, 1 = poll running.
        private int _pollInProgress;

        // Bumped for every new loadfile. Poll results that started under an
        // older generation are discarded so they cannot write stale state.
        private int _playGeneration;

        // True from file-loaded until the resume seek (if any) completes,
        // so a position read before the seek lands (0) is never treated as
        // a valid position.
        private volatile bool _resumePending;

        private int _seekRequests;

        // Ensures one EndOfFileReached per loaded file, however many signals
        // (eof-reached property, end-file reason=eof) report the same end.
        private bool _endOfFileRaised;

        public PlaybackState State { get; private set; }

        // The attempt currently loaded/loading. Set by PlayAsync.
        public PlaybackContext Context { get; private set; }

        public event EventHandler StateChanged;
        public event EventHandler TracksChanged;
        public event EventHandler EndOfFileReached;

        // Raised (on the IPC thread) once mpv confirms the selected file
        // loaded (file-loaded) - the only point at which Watch State/
        // History may be created for the attempt.
        public event EventHandler PlaybackEstablished;

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

                await _ipc.ObservePropertyAsync(ObserveIdSubDelay, "sub-delay").ConfigureAwait(true);
            }
            catch
            {
                // Non-fatal: pause/mute/volume/track state will simply stay at
                // their last-known values instead of updating live. Position/
                // duration still work since those are polled, not observed.
            }
        }

        public async Task PlayAsync(PlaybackContext context)
        {
            if (_disposed) return;

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            string url = context.PlaybackUrl;
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL cannot be empty.", nameof(context));

            if (!IsRunning)
                throw new InvalidOperationException("PlayerSession must be started before playing.");

            // Reusing a running player for another episode: mpv's keep-open
            // leaves it paused on the last frame and sub-delay would carry over,
            // so start the new file un-paused with the subtitle delay reset
            // (both are per-playback, session-only adjustments).
            bool reusingLoadedPlayer = State.IsPlaybackEstablished || State.ReachedEndOfFile;

            Context = context;
            ResetPlaybackState();

            try
            {
                await _ipc.RunCommandAsync("loadfile", url, "replace").ConfigureAwait(true);
            }
            catch (MpvCommandException)
            {
                await RecoverAndRetryPlayAsync(url).ConfigureAwait(true);
                return;
            }

            // Only AFTER the new file is queued: un-pausing first would restart
            // the old file when it is parked at its end (keep-open).
            if (reusingLoadedPlayer && !_disposed && _ipc != null)
            {
                try
                {
                    await _ipc.SetPropertyAsync("pause", false).ConfigureAwait(true);
                    await _ipc.SetPropertyAsync("sub-delay", 0.0).ConfigureAwait(true);
                }
                catch (MpvCommandException)
                {
                    // Best-effort; the observed properties report what mpv did.
                }
            }

            // The player may have been closed while loadfile was in flight.
            MpvProcessService process = _process;
            if (_disposed || process == null)
                return;

            await _host.WaitForAndAttachMpvWindowAsync(process.ProcessId, 3000).ConfigureAwait(true);
        }

        // Clears everything that describes the previous file BEFORE loadfile
        // is sent, so a late event from the new file (file-loaded can arrive
        // while we are still waiting for the mpv window) is never wiped by
        // a reset that runs afterwards, and nothing from the old file leaks
        // into the new attempt.
        private void ResetPlaybackState()
        {
            Interlocked.Increment(ref _playGeneration);
            _resumePending = false;
            _endOfFileRaised = false;

            State.ReachedEndOfFile = false;
            State.EndFileReason = null;
            State.EndFileError = null;
            State.IsPlaybackEstablished = false;
            State.HasValidPosition = false;
            State.PositionSeconds = 0;
            State.DurationSeconds = 0;

            // Loading: nothing is playing yet. The real pause state is read
            // from mpv at file-loaded (RefreshPauseStateAsync); until then the
            // UI shows a loading, not a "playing", state.
            State.IsPlaying = false;

            // Track ids/lists/selection and the subtitle delay belong to the
            // file being replaced: clear them now so nothing stale is shown
            // while the new file loads (see PlaybackTracks).
            PlaybackTracks.Reset(State);

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
            ResetPlaybackState();
            await _ipc.RunCommandAsync("loadfile", url, "replace").ConfigureAwait(true);

            MpvProcessService process = _process;
            if (_disposed || process == null)
                return;

            await _host.WaitForAndAttachMpvWindowAsync(process.ProcessId, 3000).ConfigureAwait(true);
        }

        public async Task TogglePauseAsync()
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("cycle", "pause").ConfigureAwait(true);
        }

        // Seeks are ignored until mpv has confirmed the file loaded: before
        // that there is nothing to seek, and (during a resume) a user seek
        // would race the resume seek.
        public async Task SeekRelativeAsync(double seconds)
        {
            if (!CanSeek) return;
            await _ipc.RunCommandAsync("seek", seconds, "relative").ConfigureAwait(true);
            NoteSeek(PlayerStateRules.SeekTarget(State.PositionSeconds, State.DurationSeconds, seconds, false));
        }

        public async Task SeekAbsoluteAsync(double seconds)
        {
            if (!CanSeek) return;
            await _ipc.RunCommandAsync("seek", Math.Max(0, seconds), "absolute").ConfigureAwait(true);
            NoteSeek(PlayerStateRules.SeekTarget(State.PositionSeconds, State.DurationSeconds, seconds, true));
        }

        // A user seek is only meaningful for an established file whose own
        // resume seek (if any) has finished.
        private bool CanSeek
        {
            get { return IsRunning && State.IsPlaybackEstablished && !_resumePending; }
        }

        // After a successful seek: the position (for persistence on an
        // immediate close) is the seek target until mpv's next position read,
        // and the seek counter lets the recorder persist right away. Only done
        // once a real position exists, so a seek can never fabricate one.
        private void NoteSeek(double targetSeconds)
        {
            int requests = Interlocked.Increment(ref _seekRequests);

            if (State.HasValidPosition)
            {
                State.PositionSeconds = targetSeconds;
                State.SeekCounter = requests;
                RaiseStateChanged();
            }
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

        // Track selection changes mpv's aid/sid only: playback is not
        // restarted, the source is not reloaded and the position is
        // untouched. Returns false (nothing sent) if the track no longer
        // exists in the CURRENT file's list; throws MpvCommandException if
        // mpv rejects it. Afterwards the real selection is read back from mpv,
        // so the UI reflects what mpv did rather than what was asked.
        public async Task<bool> SelectAudioTrackAsync(int trackId)
        {
            if (!IsRunning || !PlaybackTracks.HasAudioTrack(State, trackId)) return false;

            await _ipc.SetPropertyAsync("aid", trackId).ConfigureAwait(true);
            await RefreshTrackStateAsync().ConfigureAwait(true);
            return true;
        }

        // subtitleTrackId == null means "subtitles off" (sid=no).
        public async Task<bool> SelectSubtitleTrackAsync(int? subtitleTrackId)
        {
            if (!IsRunning) return false;
            if (subtitleTrackId.HasValue && !PlaybackTracks.HasSubtitleTrack(State, subtitleTrackId.Value))
                return false;

            await _ipc.SetPropertyAsync("sid", PlaybackTracks.SubtitleSelectionValue(subtitleTrackId)).ConfigureAwait(true);
            await RefreshTrackStateAsync().ConfigureAwait(true);
            return true;
        }

        // Relative change of mpv's sub-delay (session-only). The resulting
        // value is read back from mpv.
        public async Task AdjustSubtitleDelayAsync(double deltaSeconds)
        {
            if (!IsRunning) return;
            await _ipc.RunCommandAsync("add", "sub-delay", deltaSeconds).ConfigureAwait(true);
            await RefreshSubtitleDelayAsync().ConfigureAwait(true);
        }

        public async Task ResetSubtitleDelayAsync()
        {
            if (!IsRunning) return;
            await _ipc.SetPropertyAsync("sub-delay", 0.0).ConfigureAwait(true);
            await RefreshSubtitleDelayAsync().ConfigureAwait(true);
        }

        private async Task RefreshSubtitleDelayAsync()
        {
            MpvIpcClient ipc = LiveIpc();
            if (ipc == null) return;

            int generation = _playGeneration;

            try
            {
                JToken delay = await ipc.GetPropertyAsync("sub-delay").ConfigureAwait(true);

                // Result of a file that has since been replaced: ignore.
                if (generation != _playGeneration) return;

                PlaybackTracks.ApplySubtitleDelay(State, ToNullableDouble(delay));
                RaiseStateChanged();
            }
            catch (MpvCommandException)
            {
                // The observed property still updates the state.
            }
        }

        // The authoritative pause state of the newly loaded file. Reset leaves
        // IsPlaying false while loading; mpv only reports a pause CHANGE, so
        // without this read a file that simply starts playing would stay shown
        // as paused.
        private async Task RefreshPauseStateAsync()
        {
            MpvIpcClient ipc = LiveIpc();
            if (ipc == null) return;

            int generation = _playGeneration;

            try
            {
                JToken pause = await ipc.GetPropertyAsync("pause").ConfigureAwait(false);

                if (generation != _playGeneration || pause == null || pause.Type != JTokenType.Boolean)
                    return;

                State.IsPlaying = !pause.Value<bool>();
                RaiseStateChanged();
            }
            catch (MpvCommandException)
            {
                // The observed "pause" property still reports changes.
            }
        }

        // Pulls the loaded file's track list, the selected audio/subtitle
        // tracks and the subtitle delay straight from mpv. Called when a file
        // is established and after any selection. Results from a file that
        // has since been replaced are discarded (generation check).
        public async Task RefreshTrackStateAsync()
        {
            MpvIpcClient ipc = LiveIpc();
            if (ipc == null || !IsRunning) return;

            int generation = _playGeneration;

            try
            {
                JToken list = await ipc.GetPropertyAsync("track-list").ConfigureAwait(false);
                JToken aid = await ipc.GetPropertyAsync("aid").ConfigureAwait(false);
                JToken sid = await ipc.GetPropertyAsync("sid").ConfigureAwait(false);
                JToken delay = await ipc.GetPropertyAsync("sub-delay").ConfigureAwait(false);

                if (generation != _playGeneration) return;

                if (list != null && list.Type == JTokenType.Array)
                    UpdateTrackLists((JArray)list);

                // aid/sid read back as a number, or false/"no" when none.
                PlaybackTracks.ApplySelectedAudio(State, ToNullableInt(aid));
                PlaybackTracks.ApplySelectedSubtitle(State, ToNullableInt(sid));
                PlaybackTracks.ApplySubtitleDelay(State, ToNullableDouble(delay));
            }
            catch (MpvCommandException)
            {
                // mpv is shutting down or between files; observed property
                // changes keep the state current.
                return;
            }

            RaiseStateChanged();

            EventHandler tracksHandler = TracksChanged;
            if (tracksHandler != null)
                tracksHandler(this, EventArgs.Empty);
        }

        // The IPC client, or null once the session has been disposed/torn
        // down - so continuations that resume after an await never touch a
        // client that no longer exists.
        private MpvIpcClient LiveIpc()
        {
            return _disposed ? null : _ipc;
        }

        // Called by the UI's progress timer. Position/duration are polled
        // rather than observed - sub-second push updates aren't needed for a
        // seek bar, and this keeps the observed-property set small.
        public async Task PollPositionAsync()
        {
            if (!IsRunning) return;

            // Re-entrancy guard: if the previous poll is still waiting on
            // mpv, skip this tick instead of stacking another pair of
            // commands behind it.
            if (Interlocked.CompareExchange(ref _pollInProgress, 1, 0) != 0)
                return;

            try
            {
                int generation = _playGeneration;
                int seeks = Volatile.Read(ref _seekRequests);

                // Nothing is meaningful before mpv confirmed the file loaded.
                if (!State.IsPlaybackEstablished || _resumePending)
                    return;

                MpvIpcClient ipc = LiveIpc();
                if (ipc == null) return;

                JToken position = await ipc.GetPropertyAsync("time-pos").ConfigureAwait(true);
                JToken duration = await ipc.GetPropertyAsync("duration").ConfigureAwait(true);

                // A new loadfile, a pending resume seek, or a user seek started
                // while we were waiting: these values describe the wrong moment
                // (a pre-seek read must not replace the seek target).
                if (generation != _playGeneration || _resumePending || !State.IsPlaybackEstablished ||
                    seeks != Volatile.Read(ref _seekRequests))
                    return;

                double? durationValue = ToNullableDouble(duration);
                if (durationValue.HasValue && durationValue.Value > 0)
                    State.DurationSeconds = durationValue.Value;

                double? positionValue = ToNullableDouble(position);
                if (positionValue.HasValue)
                {
                    State.PositionSeconds = Math.Max(0, positionValue.Value);
                    State.HasValidPosition = true;
                    State.SeekCounter = Volatile.Read(ref _seekRequests);
                }

                RaiseStateChanged();
            }
            catch
            {
                // mpv may be between EOF, restart, or shutdown; skip this tick.
            }
            finally
            {
                Interlocked.Exchange(ref _pollInProgress, 0);
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

                case "file-loaded":
                    HandleFileLoaded();
                    break;
            }
        }

        // mpv has loaded the selected file and entered a valid playback
        // state. This is the authoritative "playback established" signal:
        // loadfile's own reply only means the command was accepted, not that
        // the stream opened (a dead or removed source replies success and
        // then fails with end-file reason=error).
        private void HandleFileLoaded()
        {
            PlaybackContext context = Context;
            if (context == null || State.IsPlaybackEstablished)
                return;

            State.IsPlaybackEstablished = true;
            _resumePending = context.HasResumePosition;

            StremioVideoCenterLogger.Info("MPV file-loaded: playback established" +
                (_resumePending ? " (resume pending)" : string.Empty));

            // Authoritatively read the new file's tracks, selection and
            // subtitle delay from mpv (in addition to the observed pushes).
            // Extension point: a future preferred-language policy would act
            // here, once the track list is known; for now mpv's own default
            // selection stands.
            var ignoredTracks = RefreshTrackStateAsync();
            var ignoredPause = RefreshPauseStateAsync();

            RaiseStateChanged();

            EventHandler established = PlaybackEstablished;
            if (established != null)
                established(this, EventArgs.Empty);

            if (_resumePending)
            {
                // Fire-and-forget: this runs on the IPC read loop, which must
                // stay free to deliver the seek's own reply.
                var ignored = ApplyResumeSeekAsync(context.StartPositionSeconds.Value);
            }
        }

        // The resume seek is asynchronous inside mpv: the command's reply only
        // means it was queued, and time-pos can still read the pre-seek
        // position (0 for a fresh file) for a moment afterwards. Persisting
        // that 0 would overwrite the very position being resumed, so the
        // pending flag is held until time-pos has actually reached the target
        // (or a short deadline passes, e.g. the seek failed, in which case
        // playback genuinely is at the start and recording it is correct).
        private async Task ApplyResumeSeekAsync(double seconds)
        {
            int generation = _playGeneration;
            double target = Math.Max(0, seconds);

            try
            {
                MpvIpcClient ipc = LiveIpc();
                if (ipc == null) return;

                await ipc.RunCommandAsync("seek", target, "absolute").ConfigureAwait(false);

                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && generation == _playGeneration && !_disposed)
                {
                    JToken position = await ipc.GetPropertyAsync("time-pos").ConfigureAwait(false);
                    double? value = ToNullableDouble(position);
                    if (value.HasValue && Math.Abs(value.Value - target) <= 5.0)
                        break;

                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch
            {
                // Resume is best-effort: on failure playback simply starts
                // from the beginning, and the saved position is not
                // overwritten until a real position is read.
            }
            finally
            {
                if (generation == _playGeneration)
                    _resumePending = false;
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

            EndFileKind kind = PlaybackEndClassifier.Classify(reason, fileError);
            bool isEof = kind == EndFileKind.Eof;
            bool isFailure = kind == EndFileKind.Failure;

            if (isEof)
                State.ReachedEndOfFile = true;

            // A failed file is not playing, whatever the last pause value was
            // (the UI must never keep showing "playing" after an error). Not
            // done for stop/quit: those also describe the OLD file when a new
            // one replaces it, and must not touch the new file's state.
            if (isFailure)
                State.IsPlaying = false;

            RaiseStateChanged();

            if (isEof)
                RaiseEndOfFileOnce();

            if (isFailure)
            {
                EventHandler failedHandler = PlaybackFailed;
                if (failedHandler != null)
                    failedHandler(this, EventArgs.Empty);
            }
        }

        private void RaiseEndOfFileOnce()
        {
            if (_endOfFileRaised)
                return;

            _endOfFileRaised = true;

            EventHandler handler = EndOfFileReached;
            if (handler != null)
                handler(this, EventArgs.Empty);
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

                case "sub-delay":
                    PlaybackTracks.ApplySubtitleDelay(State, ToNullableDouble(data));
                    break;

                case "volume":
                    double? volume = ToNullableDouble(data);
                    if (volume.HasValue)
                        State.Volume = volume.Value;
                    break;

                case "aid":
                    PlaybackTracks.ApplySelectedAudio(State, ToNullableInt(data));
                    break;

                case "sid":
                    PlaybackTracks.ApplySelectedSubtitle(State, ToNullableInt(data));
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

            // eof-reached is the normal-end signal under keep-open (see
            // ObservePropertiesAsync). Only a genuine end of an ESTABLISHED
            // file raises EndOfFileReached: never error/stop/quit.
            if (name == "eof-reached")
            {
                if (State.ReachedEndOfFile && State.IsPlaybackEstablished)
                    RaiseEndOfFileOnce();
                else if (!State.ReachedEndOfFile)
                    _endOfFileRaised = false;
            }

            if (tracksUpdated)
            {
                EventHandler tracksHandler = TracksChanged;
                if (tracksHandler != null)
                    tracksHandler(this, EventArgs.Empty);
            }
        }

        private void UpdateTrackLists(JArray trackList)
        {
            List<MpvTrackInfo> tracks = new List<MpvTrackInfo>();

            foreach (JToken token in trackList)
            {
                MpvTrackInfo track = MpvTrackParser.Parse(token as JObject);
                if (track != null)
                    tracks.Add(track);
            }

            PlaybackTracks.ApplyTrackList(State, tracks);
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
                // Bounded: a wedged pipe must not block close for the full
                // default command timeout. TeardownMpvOnly below kills the
                // process regardless.
                try { await _ipc.RunCommandWithTimeoutAsync(1000, "quit").ConfigureAwait(false); }
                catch { }

                try
                {
                    MpvProcessService process = _process;
                    if (process != null)
                        process.WaitForExit(500);
                }
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
