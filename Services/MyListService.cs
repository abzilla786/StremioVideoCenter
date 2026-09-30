using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // MY LIST SERVICE (Slice 17A - My List Core)
    // =============================================================
    //
    // Owns StremioVideoCenterSettings.MyListEntries end to end - the same
    // single-owner discipline ProfileService/WatchStateService/
    // WatchHistoryService already use. Every mutation here is immediately
    // followed by a persist call, for the same reason those services do:
    // an add/remove happens live, inside the already-open Video Center
    // window, and must survive an abrupt Playnite exit.
    //
    // Uniqueness rule: ProfileId + CanonicalKey. There must never be two
    // stored entries for the same profile's My List pointing at the same
    // piece of content - every mutating method here goes through
    // FindEntry/Add/Remove so that invariant can never be bypassed.
    //
    // UI components (media details, Hero carousel, a future My List page,
    // media cards) must all go through this service rather than touching
    // StremioVideoCenterSettings.MyListEntries directly - mirrors the
    // existing WatchStateService/WatchHistoryService discipline exactly.
    public class MyListService
    {
        private readonly StremioVideoCenterSettings _settings;
        private readonly Action _persist;

        // persist may be null (design-time/test caller) - every call site
        // below tolerates that via TryPersist, mirroring WatchStateService/
        // WatchHistoryService.
        public MyListService(StremioVideoCenterSettings settings, Action persist)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;
            _persist = persist;

            if (_settings.MyListEntries == null)
            {
                _settings.MyListEntries = new List<MyListEntry>();
            }
        }

        // =========================================================
        // READ
        // =========================================================
        //
        // Every read below filters by ProfileId first - the same
        // service-layer profile isolation WatchStateService/
        // WatchHistoryService already enforce, never left to UI
        // filtering. Ordered most-recently-added first.
        public IReadOnlyList<MyListEntry> GetEntriesForProfile(Guid profileId)
        {
            return _settings.MyListEntries
                .Where(entry => entry != null && entry.ProfileId == profileId)
                .OrderByDescending(entry => entry.AddedUtc)
                .ToList();
        }

        public bool IsInMyList(Guid profileId, string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return false;
            }

            return FindEntry(profileId, canonicalKey) != null;
        }

        // =========================================================
        // ADD
        // =========================================================
        //
        // Adding an item already present for this profile/canonical key
        // is a no-op (returns the existing entry unchanged) rather than
        // creating a duplicate or refreshing its AddedUtc - My List
        // membership, once added, does not need to "bump" back to the
        // top just because the same add is requested again.
        public MyListEntry Add(Guid profileId, MediaItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.StremioId))
            {
                // Without a canonical identity there is nothing safe to
                // key this entry on - same "cannot be resolved with what
                // we currently know" reasoning WatchStateService.
                // UpsertOnPlaybackStart already documents for a missing
                // canonicalKey.
                return null;
            }

            string canonicalKey = item.StremioId;

            MyListEntry entry = FindEntry(profileId, canonicalKey);

            if (entry != null)
            {
                return entry;
            }

            entry = new MyListEntry
            {
                ProfileId = profileId,
                CanonicalKey = canonicalKey,
                ContentType = ResolveContentType(item),
                Title = item.Title,
                PosterPath = item.PosterPath,
                Year = item.Year,
                Type = item.Type,
                Genres = item.Genres != null
                    ? new List<string>(item.Genres)
                    : new List<string>(),
                AddedUtc = DateTime.UtcNow
            };

            _settings.MyListEntries.Add(entry);

            TryPersist();

            return entry;
        }

        // =========================================================
        // REMOVE
        // =========================================================

        public void Remove(Guid profileId, string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return;
            }

            MyListEntry entry = FindEntry(profileId, canonicalKey);

            if (entry == null)
            {
                return;
            }

            _settings.MyListEntries.Remove(entry);

            TryPersist();
        }

        // =========================================================
        // TOGGLE
        // =========================================================
        //
        // Not present -> add -> returns true (now in My List).
        // Already present -> remove -> returns false (no longer in My
        // List). Deterministic, single round-trip for UI callers that
        // just want the resulting membership state (e.g. a "+"/"check"
        // button) without needing to call IsInMyList themselves first.
        public bool Toggle(Guid profileId, MediaItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.StremioId))
            {
                return false;
            }

            string canonicalKey = item.StremioId;

            MyListEntry existing = FindEntry(profileId, canonicalKey);

            if (existing != null)
            {
                _settings.MyListEntries.Remove(existing);

                TryPersist();

                return false;
            }

            Add(profileId, item);

            return true;
        }

        // =========================================================
        // PROFILE DELETION CLEANUP
        // =========================================================
        //
        // Wired to ProfileService.ProfileDeleted by whoever constructs
        // both services together (see StremioVideoCenter.cs) - "a profile
        // owns all personal media state" means deleting a profile must
        // also delete its My List, never leave it orphaned. Removes ONLY
        // that profile's entries.
        public void RemoveAllForProfile(Guid profileId)
        {
            int removed = _settings.MyListEntries.RemoveAll(
                entry => entry != null && entry.ProfileId == profileId);

            if (removed > 0)
            {
                TryPersist();
            }
        }

        // =========================================================
        // CONTENT TYPE RESOLUTION
        // =========================================================
        //
        // Mirrors StremioVideoCenterView.GetContentType(MediaItem) exactly
        // (same string values already produced throughout the codebase -
        // "Anime"/"TV"/"TV Show"/"Series"/other) - kept local here rather
        // than shared, since that method is a private view-layer helper
        // and this service must not depend on the view.
        private ContentType ResolveContentType(MediaItem item)
        {
            if (item == null)
            {
                return ContentType.Movie;
            }

            if (string.Equals(item.Type, "Anime", StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.Anime;
            }

            if (string.Equals(item.Type, "TV", StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            if (string.Equals(item.Type, "TV Show", StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            if (string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            return ContentType.Movie;
        }

        // =========================================================
        // LOOKUP
        // =========================================================

        private MyListEntry FindEntry(Guid profileId, string canonicalKey)
        {
            return _settings.MyListEntries.FirstOrDefault(entry =>
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
                // Persistence must never crash the caller - the in-memory
                // MyListEntries state above has already been updated
                // regardless, mirroring WatchStateService/
                // WatchHistoryService.TryPersist exactly.
                Playnite.SDK.LogManager.GetLogger().Warn(
                    ex,
                    "MyListService: failed to persist My List settings.");
            }
        }
    }
}
