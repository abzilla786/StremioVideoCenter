namespace StremioVideoCenterControls.Player
{
    // Remembers, for ONE open Audio/Subtitles menu, whether playback was
    // running when the menu opened, and which playback (context + load
    // generation) that was. Pure, so the rules can be unit-checked.
    //
    //   playing  -> open menu -> paused ... menu closes -> playing again
    //   paused   -> open menu -> paused ... menu closes -> still paused
    //
    // Closing the menu restores the state captured when it OPENED - it is not
    // an independent play command - and only if it is still the same
    // playback: a menu left open across an episode change, a source
    // replacement or a closed player must never resume the wrong thing.
    public sealed class MenuPlaybackGuard
    {
        private bool _held;
        private bool _wasPlaying;
        private object _context;
        private int _generation;

        public bool IsHeld { get { return _held; } }

        public bool WasPlaying { get { return _wasPlaying; } }

        // A track menu opened. Returns true when playback must be paused now.
        // Opening while a menu is already held (switching Audio <-> Subtitles)
        // keeps the ORIGINAL state and asks for nothing new.
        public bool Open(bool isPlaying, object context, int generation)
        {
            if (_held)
            {
                return false;
            }

            _held = true;
            _wasPlaying = isPlaying;
            _context = context;
            _generation = generation;

            return isPlaying;
        }

        // Still the playback the menu was opened over?
        public bool IsSamePlayback(object context, int generation)
        {
            return _held && ReferenceEquals(_context, context) && _generation == generation;
        }

        // The menu closed. Returns true only if playback should be resumed:
        // it was playing when the menu opened, it is the same playback, and it
        // is currently paused (so nothing is toggled that is already playing).
        public bool Close(object context, int generation, bool isCurrentlyPlaying)
        {
            if (!_held)
            {
                return false;
            }

            bool resume = _wasPlaying && IsSamePlayback(context, generation) && !isCurrentlyPlaying;

            Discard();

            return resume;
        }

        // Forget the menu without resuming (player disposed, playback replaced).
        public void Discard()
        {
            _held = false;
            _wasPlaying = false;
            _context = null;
            _generation = 0;
        }
    }
}
