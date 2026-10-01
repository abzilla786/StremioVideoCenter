using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // TASTE PROFILE (Slice 17C - Personalisation)
    // =============================================================
    //
    // In-memory only - NEVER persisted. A snapshot of one profile's genre
    // affinity, derived from data Zino already stores locally (My List,
    // Watch History, Watch State) by PersonalisationService. Movie, TV and
    // Anime affinity are kept strictly separate: the same genre name in two
    // content types is two unrelated entries.
    public class TasteProfile
    {
        // Minimum number of DISTINCT signal titles (movie/series-level
        // canonical keys across My List, History and Watch State) before
        // personalised recommendations are shown at all.
        public const int MinSignalTitles = 3;

        public TasteProfile(Guid profileId)
        {
            ProfileId = profileId;
            GenreAffinity = new Dictionary<ContentType, Dictionary<string, double>>();
            TypeWeight = new Dictionary<ContentType, double>();
            ExcludedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public Guid ProfileId { get; private set; }

        // Distinct movie/series-level titles that contributed a signal.
        public int SignalTitleCount { get; set; }

        // genre -> accumulated weight, per content type.
        public Dictionary<ContentType, Dictionary<string, double>> GenreAffinity { get; private set; }

        // Total weight of the genre-bearing signals per content type; only
        // types that have at least one genre appear here.
        public Dictionary<ContentType, double> TypeWeight { get; private set; }

        // Every movie/series-level key the profile already has anywhere
        // (My List, History, Watch State) - never recommended again.
        public HashSet<string> ExcludedKeys { get; private set; }

        public bool HasEnoughSignal
        {
            get { return SignalTitleCount >= MinSignalTitles && TypeWeight.Count > 0; }
        }

        // Highest-affinity genres first; ties broken alphabetically so the
        // result is fully deterministic.
        public List<string> GetTopGenres(ContentType type, int count)
        {
            Dictionary<string, double> affinity;

            if (!GenreAffinity.TryGetValue(type, out affinity))
            {
                return new List<string>();
            }

            return affinity
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .Take(count)
                .ToList();
        }
    }
}
