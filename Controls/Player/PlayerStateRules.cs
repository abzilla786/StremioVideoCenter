using System;

namespace StremioVideoCenterControls.Player
{
    // Small pure rules shared by PlayerSession and MpvPlayer so the same
    // decision is never re-derived in two places (and can be unit-checked).
    public static class PlayerStateRules
    {
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
