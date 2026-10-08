using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // Projects episode-level Watch State into the Continue Watching row.
    //
    // Watch State keeps one record per exact episode (that is required for
    // episode-specific resume, saved source, Previous/Next and auto-next) and
    // is never altered here. Continue Watching, however, is one card PER
    // SERIES (or per movie): the series' most recently played entry, shown
    // only while that entry is still incomplete.
    //
    //   S01E01 (older)  \
    //   S01E02          -> one card: the latest played episode (S01E03)
    //   S01E03 (latest) /
    //
    // If the latest played entry of a series is COMPLETED, the series has no
    // card: finishing something removes it from Continue Watching (the
    // existing completion behaviour), and an older, abandoned episode of the
    // same series must not resurface as a stale card.
    public static class ContinueWatchingProjection
    {
        public const int MaxCards = 25;

        // entries: the Watch State entries of ONE profile (the caller filters).
        // Result: at most maxCards entries, most recently played first.
        public static List<WatchStateEntry> Project(IEnumerable<WatchStateEntry> entries, int maxCards = MaxCards)
        {
            return (entries ?? Enumerable.Empty<WatchStateEntry>())
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.CanonicalKey))
                .GroupBy(SeriesKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(e => e.LastPlayedUtc)
                    // Same instant: the later episode wins.
                    .ThenByDescending(e => e.SeasonNumber ?? 0)
                    .ThenByDescending(e => e.EpisodeNumber ?? 0)
                    .First())
                .Where(latest => !latest.IsCompleted)
                .OrderByDescending(e => e.LastPlayedUtc)
                .Take(maxCards)
                .ToList();
        }

        // The show-level id for an episode ("tt…" / "kitsu:<id>"), the
        // canonical key itself for a movie.
        public static string SeriesKey(WatchStateEntry entry)
        {
            return !string.IsNullOrWhiteSpace(entry.ShowStremioId)
                ? entry.ShowStremioId
                : entry.CanonicalKey;
        }
    }
}
