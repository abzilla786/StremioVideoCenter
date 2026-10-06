using System;

namespace StremioVideoCenterControls.Player
{
    // What a Back (B / Esc / Backspace) press does inside the player.
    public enum PlayerBackAction
    {
        ReleaseSlider,
        CloseMenu,
        FocusDefaultControl,
        ExitPlayer
    }

    // Small pure rules shared by PlayerSession and MpvPlayer so the same
    // decision is never re-derived in two places (and can be unit-checked).
    public static class PlayerStateRules
    {
        // Back is menu-first: an engaged volume slider is released, then an open
        // menu is closed (restoring the pre-menu playback state), then focus on
        // a slider returns to the default control - and ONLY then does Back
        // leave the player (to the Source Selector).
        public static PlayerBackAction BackAction(bool sliderEngaged, bool menuOpen, bool sliderFocused)
        {
            if (sliderEngaged) return PlayerBackAction.ReleaseSlider;
            if (menuOpen) return PlayerBackAction.CloseMenu;
            if (sliderFocused) return PlayerBackAction.FocusDefaultControl;
            return PlayerBackAction.ExitPlayer;
        }

        // Where playback will be right after a user seek, so the position that
        // is persisted when the player closes straight afterwards is the seek
        // target rather than the stale pre-seek position. mpv's next position
        // read replaces it. Clamped to [0, duration] (duration unknown = no
        // upper bound).
        public static double SeekTarget(double currentSeconds, double durationSeconds, double seekSeconds, bool isAbsolute)
        {
            double target = isAbsolute ? seekSeconds : currentSeconds + seekSeconds;

            if (double.IsNaN(target) || double.IsInfinity(target))
                return Math.Max(0, currentSeconds);

            if (durationSeconds > 0 && target > durationSeconds)
                target = durationSeconds;

            return Math.Max(0, target);
        }

        // Keys that reach the controls overlay arrive in the overlay window's own
        // input tree, which the Video Center view's PreviewKeyDown never sees.
        // Arrow keys are therefore forwarded to the view's existing arrow
        // handling (controller-echo suppression, keyboard -> D-pad routing) so
        // one physical input produces exactly one navigation action.
        public static bool ShouldForwardKeyToHost(bool isDirectionalKey, bool hostCanHandle)
        {
            return isDirectionalKey && hostCanHandle;
        }

        // The overlay's own key handling runs unless the host already consumed
        // a forwarded key.
        public static bool ShouldHandleKeyLocally(bool forwardedToHost, bool handledByHost)
        {
            return !(forwardedToHost && handledByHost);
        }

        // Centre play-state indicator glyph. It reflects actual state only:
        //   not yet established (loading)  -> ellipsis
        //   playing                        -> play
        //   paused / ended                 -> pause
        public static string CentreGlyph(bool isEstablished, bool isPlaying)
        {
            if (!isEstablished)
                return "…";

            return isPlaying ? "▶" : "❚❚";
        }

        // Bottom Play/Pause button glyph: the ACTION it will perform
        // (playing -> pause bars, otherwise play triangle).
        public static string ActionGlyph(bool isPlaying)
        {
            return isPlaying ? "❚❚" : "▶";
        }
    }
}
