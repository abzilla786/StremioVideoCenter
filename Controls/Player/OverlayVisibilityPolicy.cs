using System;

namespace StremioVideoCenterControls.Player
{
    // Pure decision logic for the transient player overlay (bottom bar,
    // Exit button, centre play-state indicator). No WPF types, so it can be
    // unit-checked without a UI.
    //
    // The overlay is visible when ANY of these holds:
    //   - the user interacted within IdleTimeout;
    //   - playback is paused (controls never hide while paused);
    //   - a Source/Audio/Subtitle/Settings menu is open;
    //   - a seek is in progress (seek-bar drag);
    //   - playback is not actively running yet (loading, failed, or ended),
    //     so Exit is always reachable.
    // Otherwise it should fade out.
    public sealed class OverlayVisibilityPolicy
    {
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(3);

        private readonly TimeSpan _idleTimeout;
        private DateTime _lastActivityUtc;

        public OverlayVisibilityPolicy(DateTime nowUtc, TimeSpan? idleTimeout = null)
        {
            _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            _lastActivityUtc = nowUtc;
        }

        // Every mouse move, click, key, controller button, focus
        // navigation, seek, volume change and menu action calls this.
        public void NoteActivity(DateTime nowUtc)
        {
            _lastActivityUtc = nowUtc;
        }

        public bool ShouldBeVisible(
            DateTime nowUtc,
            bool isPlaybackRunning,
            bool isPaused,
            bool isMenuOpen,
            bool isSeeking)
        {
            if (!isPlaybackRunning || isPaused || isMenuOpen || isSeeking)
            {
                return true;
            }

            return nowUtc - _lastActivityUtc < _idleTimeout;
        }
    }
}
