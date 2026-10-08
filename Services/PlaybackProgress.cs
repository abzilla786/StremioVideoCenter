using StremioVideoCenter.Models;

namespace StremioVideoCenter.Services
{
    // The single place playback progress is calculated, shared by Continue
    // Watching and History (both render it through MediaCard). Watch State is
    // the only source of truth - callers pass an entry, never a stored
    // percentage.
    public static class PlaybackProgress
    {
        // Fraction in (0, 1] for an incomplete entry with a known duration;
        // 0 (meaning "show no progress bar") for a completed entry, an entry
        // with no valid duration or position, or invalid numbers.
        public static double ForEntry(WatchStateEntry entry)
        {
            if (entry == null)
            {
                return 0;
            }

            return Calculate(entry.PositionSeconds, entry.DurationSeconds, entry.IsCompleted);
        }

        public static double Calculate(double positionSeconds, double durationSeconds, bool isCompleted)
        {
            if (isCompleted ||
                double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) ||
                double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) ||
                durationSeconds <= 0 ||
                positionSeconds <= 0)
            {
                return 0;
            }

            double fraction = positionSeconds / durationSeconds;

            return fraction > 1 ? 1 : fraction;
        }
    }
}
