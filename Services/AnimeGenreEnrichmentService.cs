using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // ANIME GENRE ENRICHMENT (Slice 17C)
    // =============================================================
    //
    // Anime saved to My List / History before Slice 17C's Kitsu mapping fix
    // have empty Genres, so personalisation sees no genre signal for them.
    // This fills those in, once, WITHOUT asking the user to re-add anything:
    //
    //   targets : the ACTIVE profile's Anime My List + History entries whose
    //             CanonicalKey is "kitsu:<digits>" and whose Genres are
    //             null/empty. Nothing else is ever touched, and existing
    //             non-empty Genres are never overwritten.
    //   network : batched - ONE Kitsu request per up to 20 ids
    //             (KitsuService.GetCategoryTitlesByIdsAsync), at most
    //             MaxBatchesPerRun requests per call, never one per entry.
    //   persist : through MyListService/WatchHistoryService.SetGenresIfEmpty
    //             (their normal persist path), never the config file directly.
    //   repeats : every id requested is remembered for the app session, so a
    //             failure or a genuinely category-less title is not re-asked on
    //             each refresh (it is retried on the next app start). Resolved
    //             genres are also remembered, so a second profile holding the
    //             same title is filled without another request.
    //
    // Never throws except OperationCanceledException.
    public class AnimeGenreEnrichmentService
    {
        private const string KitsuPrefix = "kitsu:";
        private const int BatchSize = 20;
        private const int MaxBatchesPerRun = 3;

        private readonly MyListService _myListService;
        private readonly WatchHistoryService _watchHistoryService;
        private readonly Func<IList<string>, Task<KitsuCategoryLookupResult>> _lookup;

        private readonly HashSet<string> _attemptedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<string>> _genresById =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public AnimeGenreEnrichmentService(
            MyListService myListService,
            WatchHistoryService watchHistoryService,
            Func<IList<string>, Task<KitsuCategoryLookupResult>> lookup)
        {
            _myListService = myListService;
            _watchHistoryService = watchHistoryService;
            _lookup = lookup;
        }

        public async Task EnrichAsync(Guid profileId, CancellationToken cancellationToken)
        {
            try
            {
                await EnrichCoreAsync(profileId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Anime genre enrichment failed: " + ex.GetType().Name);
            }
        }

        private async Task EnrichCoreAsync(Guid profileId, CancellationToken cancellationToken)
        {
            // 1. Targets: this profile's Kitsu anime entries without genres.
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_myListService != null)
            {
                foreach (MyListEntry entry in _myListService.GetEntriesForProfile(profileId))
                {
                    if (entry != null && NeedsGenres(entry.ContentType, entry.CanonicalKey, entry.Genres))
                    {
                        keys.Add(entry.CanonicalKey);
                    }
                }
            }

            if (_watchHistoryService != null)
            {
                foreach (WatchHistoryEntry entry in _watchHistoryService.GetEntriesForProfile(profileId))
                {
                    if (entry != null && NeedsGenres(entry.ContentType, entry.CanonicalKey, entry.Genres))
                    {
                        keys.Add(entry.CanonicalKey);
                    }
                }
            }

            if (keys.Count == 0)
            {
                return;
            }

            // 2. Reuse anything already resolved this session; collect the
            // ids that still need a request (never one already attempted).
            var resolved = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var idsToRequest = new List<string>();

            foreach (string key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string id = key.Substring(KitsuPrefix.Length);
                List<string> known;

                if (_genresById.TryGetValue(id, out known))
                {
                    resolved[key] = known;
                }
                else if (!_attemptedIds.Contains(id))
                {
                    idsToRequest.Add(id);
                }
            }

            StremioVideoCenterLogger.Info(
                "Anime genre enrichment: entries needing genres=" + keys.Count +
                " reusedFromSession=" + resolved.Count +
                " toRequest=" + idsToRequest.Count);

            // 3. Batched requests: up to BatchSize ids each.
            int batches = 0;

            for (int offset = 0;
                 offset < idsToRequest.Count && batches < MaxBatchesPerRun;
                 offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<string> batch = idsToRequest.Skip(offset).Take(BatchSize).ToList();
                batches++;

                // Marked before the request so an overlapping refresh does
                // not issue it again; a failure is retried next app start.
                foreach (string id in batch)
                {
                    _attemptedIds.Add(id);
                }

                KitsuCategoryLookupResult result;

                try
                {
                    result = await _lookup(batch);
                }
                catch (Exception ex)
                {
                    StremioVideoCenterLogger.Info(
                        "Anime genre enrichment batch failed: requested=" + batch.Count +
                        " error=" + ex.GetType().Name);
                    continue;
                }

                if (result == null || result.TitlesById == null)
                {
                    continue;
                }

                int withCategories = 0;

                foreach (KeyValuePair<string, List<string>> pair in result.TitlesById)
                {
                    if (pair.Value == null || pair.Value.Count == 0)
                    {
                        continue;
                    }

                    withCategories++;
                    _genresById[pair.Key] = pair.Value;
                    resolved[KitsuPrefix + pair.Key] = pair.Value;
                }

                // returned > 0 with matched == 0 would mean Kitsu ignored
                // filter[id] and returned unrelated anime (nothing applied).
                StremioVideoCenterLogger.Info(
                    "Anime genre enrichment batch: requested=" + result.RequestedCount +
                    " returned=" + result.ReturnedCount +
                    " matched=" + result.TitlesById.Count +
                    " withCategories=" + withCategories);
            }

            // 4. Apply to THIS profile's entries only (empty-only, persisted
            // by the services themselves).
            if (resolved.Count == 0)
            {
                return;
            }

            int myListUpdated = _myListService != null
                ? _myListService.SetGenresIfEmpty(profileId, resolved)
                : 0;

            int historyUpdated = _watchHistoryService != null
                ? _watchHistoryService.SetGenresIfEmpty(profileId, resolved)
                : 0;

            StremioVideoCenterLogger.Info(
                "Anime genre enrichment applied: myListUpdated=" + myListUpdated +
                " historyUpdated=" + historyUpdated);
        }

        // ContentType == Anime, key exactly "kitsu:<digits>", Genres empty.
        private static bool NeedsGenres(ContentType type, string canonicalKey, List<string> genres)
        {
            if (type != ContentType.Anime ||
                (genres != null && genres.Count > 0) ||
                string.IsNullOrWhiteSpace(canonicalKey) ||
                !canonicalKey.StartsWith(KitsuPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string id = canonicalKey.Substring(KitsuPrefix.Length);

            return id.Length > 0 && id.All(char.IsDigit);
        }
    }
}
