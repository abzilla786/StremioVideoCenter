using StremioVideoCenter.Models;
using System;

namespace StremioVideoCenter.Services
{
    // Feeds playback progress into WatchStateService for ONE established
    // playback attempt.
    //
    // - Polling stays in memory: Update() is called on every progress tick
    //   and only mutates the Watch State entry in memory.
    // - Disk persistence is throttled (PersistInterval) and additionally
    //   forced on pause/resume, seek and end-of-file, and by Flush() at
    //   close/failure.
    // - Only a VALID position is ever recorded: PlaybackState.HasValidPosition
    //   is true only once mpv confirmed the file loaded, a numeric time-pos
    //   was read, and no resume seek is pending. Position 0 is therefore
    //   recorded when it is a genuine start, and an unstarted/aborted
    //   attempt can never overwrite a previously saved position.
    //
    // The profile is the one captured in the PlaybackContext - never
    // re-read from the live active profile.
    //
    // Created only after playback was established and the Watch State entry
    // exists (see StremioVideoCenterView), so a failed or aborted attempt
    // has no recorder and writes nothing.
    public sealed class PlaybackProgressRecorder
    {
        public static readonly TimeSpan DefaultPersistInterval = TimeSpan.FromSeconds(15);

        private readonly WatchStateService _watchState;
        private readonly PlaybackContext _context;
        private readonly TimeSpan _persistInterval;
        private readonly Func<DateTime> _clock;

        private DateTime _lastPersistUtc;
        private bool _dirty;
        private bool _hasLastSnapshot;
        private bool _lastIsPlaying;
        private int _lastSeekCounter;
        private bool _lastReachedEof;

        public PlaybackProgressRecorder(
            WatchStateService watchState,
            PlaybackContext context,
            TimeSpan? persistInterval = null,
            Func<DateTime> clock = null)
        {
            if (watchState == null) throw new ArgumentNullException(nameof(watchState));
            if (context == null) throw new ArgumentNullException(nameof(context));

            _watchState = watchState;
            _context = context;
            _persistInterval = persistInterval ?? DefaultPersistInterval;
            _clock = clock ?? (() => DateTime.UtcNow);
            _lastPersistUtc = _clock();
        }

        // Called on every progress tick. Returns true if it wrote to disk.
        public bool Update(PlaybackState state)
        {
            if (state == null || !state.IsPlaybackEstablished || !state.HasValidPosition)
            {
                return false;
            }

            _watchState.RecordProgress(
                _context.ProfileId,
                _context.CanonicalKey,
                state.PositionSeconds,
                state.DurationSeconds,
                state.ReachedEndOfFile,
                persist: false);
            _dirty = true;

            bool important =
                _hasLastSnapshot &&
                (state.IsPlaying != _lastIsPlaying ||
                 state.SeekCounter != _lastSeekCounter ||
                 (state.ReachedEndOfFile && !_lastReachedEof));

            _hasLastSnapshot = true;
            _lastIsPlaying = state.IsPlaying;
            _lastSeekCounter = state.SeekCounter;
            _lastReachedEof = state.ReachedEndOfFile;

            if (important || _clock() - _lastPersistUtc >= _persistInterval)
            {
                return PersistNow();
            }

            return false;
        }

        // Immediate persist of the last valid position: close, failure,
        // completion. If the state never had a valid position this does
        // nothing, leaving any previously saved position intact.
        public bool Flush(PlaybackState state)
        {
            if (state != null && state.IsPlaybackEstablished && state.HasValidPosition)
            {
                _watchState.RecordProgress(
                    _context.ProfileId,
                    _context.CanonicalKey,
                    state.PositionSeconds,
                    state.DurationSeconds,
                    state.ReachedEndOfFile,
                    persist: false);
                _dirty = true;
            }

            return _dirty && PersistNow();
        }

        // Genuine end of file: record the final valid progress (if any) and
        // mark exactly this attempt's entry completed, persisted immediately.
        // Completion does not depend on a valid position having been read.
        public void Complete(PlaybackState state)
        {
            if (state != null && state.IsPlaybackEstablished && state.HasValidPosition)
            {
                _watchState.RecordProgress(
                    _context.ProfileId,
                    _context.CanonicalKey,
                    state.PositionSeconds,
                    state.DurationSeconds,
                    true,
                    persist: false);
            }

            _watchState.MarkCompleted(_context.ProfileId, _context.CanonicalKey);
            _lastPersistUtc = _clock();
            _dirty = false;
        }

        private bool PersistNow()
        {
            _watchState.Flush();
            _lastPersistUtc = _clock();
            _dirty = false;
            return true;
        }
    }
}
