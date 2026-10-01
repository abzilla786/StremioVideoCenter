using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // WATCH HISTORY SERVICE (Slice 16C - Continue Watching + History)
    // =============================================================
    //
    // Owns StremioVideoCenterSettings.WatchHistory end to end - same
    // single-owner discipline as ProfileService (Profiles/
    // ActiveProfileId) and WatchStateService (WatchStates). Every
    // mutation persists immediately, for the same "must survive an
    // abrupt exit" reasoning those two already document.
    //
    // Uniqueness rule: ProfileId + CanonicalKey, where CanonicalKey is
    // always the SHOW/movie-level identity (see WatchHistoryEntry class
    // remarks) - a TV/anime series always has exactly one History entry
    // per profile no matter how many episodes are watched, updated in
    // place rather than duplicated.
    public class WatchHistoryService
    {
        // "Maximum 200 History entries per profile" (Slice 16C spec,
        // Section 19).
        public const int MaxEntriesPerProfile = 200;

        private readonly StremioVideoCenterSettings _settings;
        private readonly Action _persist;

        public WatchHistoryService(StremioVideoCenterSettings settings, Action persist)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;
            _persist = persist;

            if (_settings.WatchHistory == null)
            {
                _settings.WatchHistory = new List<WatchHistoryEntry>();
            }
        }

        // =========================================================
        // READ
        // =========================================================
        //
        // Every read filters by ProfileId first - "must not rely solely
        // on UI filtering" (Slice 16C spec, Section 9/12) is enforced
        // here, not by callers. Ordered most-recently-watched first, the
        // same ordering Continue Watching uses for Watch State.
        public IReadOnlyList<WatchHistoryEntry> GetEntriesForProfile(Guid profileId)
        {
            return _settings.WatchHistory
                .Where(entry => entry != null && entry.ProfileId == profileId)
                .OrderByDescending(entry => entry.LastWatchedUtc)
                .ToList();
        }

        // =========================================================
        // RECORD VISIT - update/create the series/movie-level entry
        // =========================================================
        //
        // Called from the same "playback actually started" moment
        // WatchStateService.UpsertOnPlaybackStart is (Slice 16C spec,
        // Section 16). seriesCanonicalKey is always MediaItem.StremioId
        // (see class remarks); latestWatchedCanonicalKey is the exact
        // Watch State CanonicalKey for the specific thing just played.
        public WatchHistoryEntry RecordVisit(
            Guid profileId,
            ContentType contentType,
            string seriesCanonicalKey,
            string latestWatchedCanonicalKey,
            int? latestSeasonNumber,
            int? latestEpisodeNumber,
            string title,
            string posterPath,
            string year,
            string displayType,
            IEnumerable<string> genres = null)
        {
            if (string.IsNullOrWhiteSpace(seriesCanonicalKey))
            {
                return null;
            }

            WatchHistoryEntry entry = FindEntry(profileId, seriesCanonicalKey);

            if (entry == null)
            {
                entry = new WatchHistoryEntry
                {
                    ProfileId = profileId,
                    CanonicalKey = seriesCanonicalKey
                };

                _settings.WatchHistory.Add(entry);
            }

            // "Rewatching the same movie should update its existing
            // History entry rather than creating duplicates" / "when a
            // new episode is watched, update the existing anime/TV
            // History entry to the latest watched episode" (Slice 16C
            // spec, Sections 13/15/16) - the same entry is always reused,
            // never duplicated, because FindEntry above keys on the
            // series/movie-level CanonicalKey alone.
            entry.ContentType = contentType;
            entry.LatestWatchedCanonicalKey = latestWatchedCanonicalKey;
            entry.LatestSeasonNumber = latestSeasonNumber;
            entry.LatestEpisodeNumber = latestEpisodeNumber;
            entry.Title = title;
            entry.PosterPath = posterPath;
            entry.Year = year;
            entry.Type = displayType;
            entry.LastWatchedUtc = DateTime.UtcNow;

            // Slice 17C: only ever replaced by a non-empty genre list, so a
            // replay started from a source that carries no genres (e.g.
            // Continue Watching) never erases genres recorded earlier.
            if (genres != null)
            {
                List<string> cleaned = genres
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(genre => genre.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (cleaned.Count > 0)
                {
                    entry.Genres = cleaned;
                }
            }

            EnforceRetentionCap(profileId);

            TryPersist();

            return entry;
        }

        // =========================================================
        // PROFILE DELETION CLEANUP
        // =========================================================
        //
        // Wired to ProfileService.ProfileDeleted alongside WatchStateService
        // .RemoveAllForProfile (see StremioVideoCenter.cs) - "deleting a
        // profile must not leave orphaned History records" (Slice 16C
        // spec, Section 24).
        public void RemoveAllForProfile(Guid profileId)
        {
            int removed = _settings.WatchHistory.RemoveAll(
                entry => entry != null && entry.ProfileId == profileId);

            if (removed > 0)
            {
                TryPersist();
            }
        }

        // =========================================================
        // RETENTION CAP
        // =========================================================
        //
        // "Remove the oldest appropriate History entries based on
        // LastWatched/LastVisited time. Do not remove the currently
        // active entry unnecessarily." (Slice 16C spec, Section 19) -
        // simple oldest-first eviction by LastWatchedUtc already
        // satisfies this: the entry that was just touched by RecordVisit
        // above has the newest possible LastWatchedUtc, so it is
        // mathematically the last entry this loop could ever pick.
        private void EnforceRetentionCap(Guid profileId)
        {
            List<WatchHistoryEntry> profileEntries = _settings.WatchHistory
                .Where(entry => entry != null && entry.ProfileId == profileId)
                .ToList();

            int overflow = profileEntries.Count - MaxEntriesPerProfile;

            if (overflow <= 0)
            {
                return;
            }

            IEnumerable<WatchHistoryEntry> evictionOrder = profileEntries
                .OrderBy(entry => entry.LastWatchedUtc);

            foreach (WatchHistoryEntry entry in evictionOrder.Take(overflow))
            {
                _settings.WatchHistory.Remove(entry);
            }
        }

        // =========================================================
        // LOOKUP
        // =========================================================

        private WatchHistoryEntry FindEntry(Guid profileId, string canonicalKey)
        {
            return _settings.WatchHistory.FirstOrDefault(entry =>
                entry != null &&
                entry.ProfileId == profileId &&
                string.Equals(
                    entry.CanonicalKey,
                    canonicalKey,
                    StringComparison.OrdinalIgnoreCase));
        }

        // =========================================================
        // PERSIST
        // =========================================================

        private void TryPersist()
        {
            try
            {
                _persist?.Invoke();
            }
            catch (Exception ex)
            {
                Playnite.SDK.LogManager.GetLogger().Warn(
                    ex,
                    "WatchHistoryService: failed to persist watch history settings.");
            }
        }
    }
}
