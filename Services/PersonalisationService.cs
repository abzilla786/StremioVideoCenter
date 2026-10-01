using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // PERSONALISATION SERVICE (Slice 17C)
    // =============================================================
    //
    // Local, deterministic, explainable. No randomness, no external
    // recommendation API, nothing persisted, no per-item metadata fetches.
    //
    // SIGNALS (one per distinct movie/series-level canonical key; if a
    // title appears in several stores the strongest signal wins, never a
    // sum - episodes are never separate targets):
    //   My List              weight 3
    //   Completed (State)    weight 2
    //   Started   (History/State) weight 1
    // each multiplied by recency: <= 30 days 1.0, <= 90 days 0.6, older 0.3.
    //
    // The "engagement bonus" for repeated completed episodes from the
    // audit is deliberately OMITTED: Watch State is capped/evicts
    // completed entries first, so the count is unstable.
    //
    // GENRE AFFINITY = sum of title weights per genre, kept separately for
    // Movie / TV / Anime. Genres come from My List entries and (new in
    // 17C) Watch History entries; old history without genres, and Watch
    // State, contribute to the title count/exclusions only.
    //
    // THRESHOLD: recommendations need at least TasteProfile.MinSignalTitles
    // (3) distinct signal titles AND at least one genre-bearing signal.
    public class PersonalisationService
    {
        public const int RowSize = 25;
        private const int MaxGenresPerType = 2;
        private const int MinCandidatesBeforeStopping = 12;

        private static readonly ContentType[] TypeOrder =
        {
            ContentType.Movie,
            ContentType.TvShow,
            ContentType.Anime
        };

        private readonly MyListService _myListService;
        private readonly WatchHistoryService _watchHistoryService;
        private readonly WatchStateService _watchStateService;

        public PersonalisationService(
            MyListService myListService,
            WatchHistoryService watchHistoryService,
            WatchStateService watchStateService)
        {
            _myListService = myListService;
            _watchHistoryService = watchHistoryService;
            _watchStateService = watchStateService;
        }

        private class Signal
        {
            public ContentType Type;
            public double Weight;
            public List<string> Genres = new List<string>();
        }

        // =========================================================
        // TASTE PROFILE (pure local calculation)
        // =========================================================

        public TasteProfile BuildTasteProfile(Guid profileId, DateTime nowUtc)
        {
            var taste = new TasteProfile(profileId);
            var signals = new Dictionary<string, Signal>(StringComparer.OrdinalIgnoreCase);

            // Watch State first: needed to know which shows are completed.
            IReadOnlyList<WatchStateEntry> states = _watchStateService != null
                ? _watchStateService.GetEntriesForProfile(profileId)
                : new List<WatchStateEntry>();

            var completedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (WatchStateEntry state in states)
            {
                if (state == null)
                {
                    continue;
                }

                string key = ToSeriesKey(state.ShowStremioId, state.CanonicalKey, state.ContentType);

                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (state.IsCompleted)
                {
                    completedKeys.Add(key);
                }

                Merge(
                    signals,
                    key,
                    state.ContentType,
                    (state.IsCompleted ? 2.0 : 1.0) * Recency(state.LastPlayedUtc, nowUtc),
                    null);
            }

            if (_watchHistoryService != null)
            {
                foreach (WatchHistoryEntry history in _watchHistoryService.GetEntriesForProfile(profileId))
                {
                    if (history == null || string.IsNullOrWhiteSpace(history.CanonicalKey))
                    {
                        continue;
                    }

                    Merge(
                        signals,
                        history.CanonicalKey,
                        history.ContentType,
                        (completedKeys.Contains(history.CanonicalKey) ? 2.0 : 1.0) * Recency(history.LastWatchedUtc, nowUtc),
                        history.Genres);
                }
            }

            if (_myListService != null)
            {
                foreach (MyListEntry entry in _myListService.GetEntriesForProfile(profileId))
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.CanonicalKey))
                    {
                        continue;
                    }

                    Merge(
                        signals,
                        entry.CanonicalKey,
                        entry.ContentType,
                        3.0 * Recency(entry.AddedUtc, nowUtc),
                        entry.Genres);
                }
            }

            taste.SignalTitleCount = signals.Count;

            foreach (KeyValuePair<string, Signal> pair in signals)
            {
                taste.ExcludedKeys.Add(pair.Key);

                Signal signal = pair.Value;

                if (signal.Genres.Count == 0)
                {
                    continue;
                }

                Dictionary<string, double> affinity;

                if (!taste.GenreAffinity.TryGetValue(signal.Type, out affinity))
                {
                    affinity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    taste.GenreAffinity[signal.Type] = affinity;
                }

                foreach (string genre in signal.Genres)
                {
                    double existing;
                    affinity.TryGetValue(genre, out existing);
                    affinity[genre] = existing + signal.Weight;
                }

                double typeWeight;
                taste.TypeWeight.TryGetValue(signal.Type, out typeWeight);
                taste.TypeWeight[signal.Type] = typeWeight + signal.Weight;
            }

            return taste;
        }

        private static void Merge(
            Dictionary<string, Signal> signals,
            string key,
            ContentType type,
            double weight,
            IEnumerable<string> genres)
        {
            Signal signal;

            if (!signals.TryGetValue(key, out signal))
            {
                signal = new Signal { Type = type, Weight = weight };
                signals[key] = signal;
            }
            else if (weight > signal.Weight)
            {
                signal.Weight = weight;
            }

            if (signal.Genres.Count == 0 && genres != null)
            {
                signal.Genres = genres
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(genre => genre.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static double Recency(DateTime utc, DateTime nowUtc)
        {
            if (utc == default(DateTime))
            {
                return 0.3;
            }

            double ageDays = (nowUtc - utc).TotalDays;

            if (ageDays <= 30)
            {
                return 1.0;
            }

            return ageDays <= 90 ? 0.6 : 0.3;
        }

        // Watch State is per-episode; the show/movie-level key is what
        // identifies a recommendation target. ShowStremioId is normally
        // populated - the fallback strips an episode suffix defensively.
        private static string ToSeriesKey(string showStremioId, string canonicalKey, ContentType type)
        {
            if (!string.IsNullOrWhiteSpace(showStremioId))
            {
                return showStremioId;
            }

            if (string.IsNullOrWhiteSpace(canonicalKey) || type == ContentType.Movie)
            {
                return canonicalKey;
            }

            string[] parts = canonicalKey.Split(':');

            if (parts.Length >= 2 && string.Equals(parts[0], "kitsu", StringComparison.OrdinalIgnoreCase))
            {
                return parts[0] + ":" + parts[1];
            }

            return parts[0];
        }

        // =========================================================
        // RECOMMENDATIONS (a few cached catalogue calls)
        // =========================================================
        //
        // Throws OperationCanceledException when the token is cancelled -
        // callers use that to discard stale (previous-profile) work.
        // additionalExcludedKeys carries keys known only to the UI (e.g.
        // current Hero items).
        public async Task<List<MediaItem>> GetRecommendationsAsync(
            TasteProfile taste,
            CatalogueService catalogue,
            ISet<string> additionalExcludedKeys,
            CancellationToken cancellationToken)
        {
            var empty = new List<MediaItem>();

            if (taste == null || catalogue == null || !taste.HasEnoughSignal)
            {
                return empty;
            }

            var types = TypeOrder.Where(type => taste.TypeWeight.ContainsKey(type)).ToList();

            var tasks = types
                .Select(type => GatherCandidatesAsync(type, taste, catalogue, additionalExcludedKeys, cancellationToken))
                .ToList();

            List<MediaItem>[] perType = await Task.WhenAll(tasks);

            cancellationToken.ThrowIfCancellationRequested();

            return Interleave(types, perType, taste);
        }

        private static async Task<List<MediaItem>> GatherCandidatesAsync(
            ContentType type,
            TasteProfile taste,
            CatalogueService catalogue,
            ISet<string> additionalExcludedKeys,
            CancellationToken cancellationToken)
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int queried = 0;

            // A few more genres than will be queried, so unusable ones
            // (not in Cinemeta's own genre list) can be skipped.
            foreach (string genre in taste.GetTopGenres(type, 6))
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<MediaItem> items = await FetchGenreAsync(type, genre, catalogue);

                if (items == null)
                {
                    continue;
                }

                foreach (MediaItem item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.StremioId))
                    {
                        continue;
                    }

                    if (taste.ExcludedKeys.Contains(item.StremioId) ||
                        (additionalExcludedKeys != null && additionalExcludedKeys.Contains(item.StremioId)))
                    {
                        continue;
                    }

                    if (seen.Add(item.StremioId))
                    {
                        result.Add(item);
                    }
                }

                if (++queried >= MaxGenresPerType || result.Count >= MinCandidatesBeforeStopping)
                {
                    break;
                }
            }

            return result;
        }

        // Returns null when the genre cannot be queried for this type (not
        // in Cinemeta's own genre list / no usable Kitsu slug) so it does
        // not count against MaxGenresPerType. A failed request yields an
        // empty list - a personalisation failure must never surface.
        private static async Task<List<MediaItem>> FetchGenreAsync(
            ContentType type,
            string genre,
            CatalogueService catalogue)
        {
            try
            {
                if (type == ContentType.Anime)
                {
                    string slug = ToKitsuSlug(genre);

                    return string.IsNullOrEmpty(slug)
                        ? null
                        : await catalogue.GetAnimeCategoryRecommendationCandidatesAsync(slug);
                }

                bool isTv = type == ContentType.TvShow;

                IReadOnlyList<string> known = isTv
                    ? CatalogueService.SeriesGenres
                    : CatalogueService.MovieGenres;

                string canonical = known.FirstOrDefault(
                    g => string.Equals(g, genre, StringComparison.OrdinalIgnoreCase));

                return canonical == null
                    ? null
                    : await catalogue.GetGenreRecommendationCandidatesAsync(isTv, canonical);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new List<MediaItem>();
            }
        }

        // Kitsu category slugs are the lower-case, hyphenated form of the
        // category title. Kitsu has no categories-listing endpoint to
        // verify this against; an unmatched slug just returns no items.
        private static string ToKitsuSlug(string genre)
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                return null;
            }

            var builder = new StringBuilder();
            bool lastWasHyphen = true;

            foreach (char c in genre.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    lastWasHyphen = false;
                }
                else if (!lastWasHyphen)
                {
                    builder.Append('-');
                    lastWasHyphen = true;
                }
            }

            return builder.ToString().Trim('-');
        }

        // Smooth weighted round-robin over the content types by their
        // affinity weight: deterministic, proportional, and automatically
        // gives the remaining slots to the types that still have
        // candidates. Order inside a type is the catalogue's own order.
        private static List<MediaItem> Interleave(
            List<ContentType> types,
            List<MediaItem>[] perType,
            TasteProfile taste)
        {
            var result = new List<MediaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var positions = new int[types.Count];
            var current = new double[types.Count];

            while (result.Count < RowSize)
            {
                double totalAvailable = 0;
                int best = -1;

                for (int i = 0; i < types.Count; i++)
                {
                    if (positions[i] >= perType[i].Count)
                    {
                        continue;
                    }

                    double weight = taste.TypeWeight[types[i]];
                    totalAvailable += weight;
                    current[i] += weight;

                    if (best < 0 || current[i] > current[best])
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                current[best] -= totalAvailable;

                MediaItem item = perType[best][positions[best]++];

                if (seen.Add(item.StremioId))
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }
}
