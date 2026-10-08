using System;
using System.Globalization;

namespace StremioVideoCenterControls.Player
{
    // Pure helpers for the session-only subtitle delay adjustment (mpv's
    // sub-delay). Positive = subtitles appear later, negative = earlier.
    public static class SubtitleDelay
    {
        public const double StepSeconds = 0.5;

        // "0.0s", "+0.5s", "-1.0s". Values that round to zero (including the
        // tiny floating-point residue mpv can report) display as "0.0s".
        public static string Format(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || Math.Abs(seconds) < 0.05)
                seconds = 0;

            string text = seconds.ToString("0.0", CultureInfo.InvariantCulture);
            return (seconds > 0 ? "+" : string.Empty) + text + "s";
        }
    }
}
