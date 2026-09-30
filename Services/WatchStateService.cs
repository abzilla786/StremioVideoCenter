using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // WATCH STATE SERVICE (Slice 16B - Persistent Profile-Owned Watch State)
    // =============================================================
    //
    // Owns StremioVideoCenterSettings.WatchStates end to end - the same
    // single-owner discipline ProfileService already uses for Profiles/
    // ActiveProfileId, and LocalLibraryService for LibraryLocations.
    // Every mutation here is immediately followed by a persist call, for
    // the same reason ProfileService does: playback progress happens
    // live, inside the already-open Video Center window, and must
    // survive an abrupt Playnite exit without any Settings-dialog Save.
    //
    // Uniqueness rule: ProfileId + CanonicalKey. There must never be two
    // stored entries for the same profile watching the same piece of
    // content - every mutating method here goes through FindEntry/Upsert
    // so that invariant can never be bypassed.
    //
    // This class knows nothing about playback/mpv/UI - it is handed
    // already-known progress numbers by whatever owns the actual
    // playback session (StremioVideoCenterView, via the existing
    // PlayerSession.PollPositionAsync-driven polling loop already used
    // for the seek bar - see MpvPlayer.PlaybackStateChanged remarks). No
    // second timer or position-polling mechanism is introduced here.
    public class WatchStateService
    {
        // "Maximum 500 Watch State entries per profile" (Slice 16B spec,
        // Section 12).
        public const int MaxEntriesPerProfile = 500;

        // "A Watch State entry becomes completed when either the player
        // reports EOF, or playback reaches approximately 90% of known
        // duration" (Slice 16B spec, Section 10) - not configurable, not
        // a different threshold anywhere else in this class.
        private const double CompletionFraction = 0.90;

        private readonly StremioVideoCenterSettings _settings;
        private readonly Action _persist;

        // persist may be null (design-time/test caller) - every call site
        // below tolerates that via TryPersist, mirroring ProfileService.
        public WatchStateService(StremioVideoCenterSettings settings, Action persist)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;
            _persist = persist;

            if (_settings.WatchStates == null)
            {
                _settings.WatchStates = new List<WatchStateEntry>();
            }
        }

        // =========================================================
        // READ
        // =========================================================

        // Every read below filters by ProfileId first - Section 8 of the
        // spec ("the service/data layer itself must be profile-aware",
        // "do not rely solely on UI filtering") is enforced here, not by
        // callers.
        public IReadOnlyList<WatchStateEntry> GetEntriesForProfile(Guid profileId)
        {
            return _settings.WatchStates
                .Where(entry => entry != null && entry.ProfileId == profileId)
                .ToList();
        }

        public WatchStateEntry GetEntry(Guid profileId, string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return null;
            }

            return FindEntry(profileId, canonicalKey);
        }

        // =========================================================
        // PLAYBACK STARTED - establish/update the entry
        // =========================================================
        //
        // Called once when a playback session actually begins (source
        // selected, mpv handed the url) - "Establish/update the Watch
        // State entry for the current profile/content" (Slice 16B spec,
        // Section 9). Position/duration are whatever is already known at
        // that moment (usually 0/0 - the first PollPositionAsync tick
        // fills these in via RecordProgress shortly after) rather than
        // left unset, so an entry always exists as soon as playback
        // starts, even if playback is stopped before a single progress
        // tick arrives.
        public WatchStateEntry UpsertOnPlaybackStart(
            Guid profileId,
            ContentType contentType,
            string canonicalKey,
            string showStremioId,
            int? seasonNumber,
            int? episodeNumber,
            string title,
            string posterPath,
            string year,
            string displayType)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                // Without a canonical identity there is nothing safe to
                // key this entry on - callers must treat this as "watch
                // state unavailable for this item" rather than inventing
                // a fallback identity (see ContentRequest.BuildStremioId
                // remarks: null already means "cannot be resolved with
                // what we currently know").
                return null;
            }

            DateTime now = DateTime.UtcNow;

            WatchStateEntry entry = FindEntry(profileId, canonicalKey);

            if (entry == null)
            {
                entry = new WatchStateEntry
                {
                    ProfileId = profileId,
                    CanonicalKey = canonicalKey,
                    PositionSeconds = 0,
                    DurationSeconds = 0,
                    IsCompleted = false
                };

                _settings.WatchStates.Add(entry);
            }

            // Re-playing existing content (e.g. rewatching a completed
            // episode) resumes/updates the SAME entry rather than
            // creating a duplicate - "start the same content again and
            // confirm the existing Watch State is updated rather than
            // duplicated" (Slice 16B spec, Section 20).
            //
            // A deliberate rewatch (starting playback again) is the one
            // and only thing that transitions a completed entry back to
            // active/incomplete - RecordProgress's completion latch is
            // intentionally one-directional (rewinding near the end of a
            // completed item must not un-complete it), and this is the
            // designed escape hatch from that latch, exactly as already
            // documented in RecordProgress's own remarks below. Position/
            // duration are left untouched here (not reset to 0) - the
            // very next RecordProgress tick overwrites them with the new
            // session's real values within moments, the same way a brand
            // new entry's 0/0 starting values are immediately superseded.
            entry.IsCompleted = false;

            entry.ContentType = contentType;
            entry.ShowStremioId = showStremioId;
            entry.SeasonNumber = seasonNumber;
            entry.EpisodeNumber = episodeNumber;
            entry.Title = title;
            entry.PosterPath = posterPath;
            entry.Year = year;
            entry.Type = displayType;
            entry.LastPlayedUtc = now;

            EnforceRetentionCap(profileId);

            TryPersist();

            return entry;
        }

        // =========================================================
        // RECORD PROGRESS (during playback / on close)
        // =========================================================
        //
        // Driven by the existing PlayerSession.PollPositionAsync polling
        // loop (via MpvPlayer.PlaybackStateChanged - see that control's
        // remarks) - never a second timer. Also called one final time
        // from StremioVideoCenterView.ClosePlayer() with the last known
        // state before the player is torn down, so a position update
        // that would otherwise be lost in the gap between the last poll
        // tick and the user closing playback is not discarded - "ensure
        // the latest known playback state is persisted before playback
        // is discarded" (Slice 16B spec, Section 9).
        //
        // No-op (returns null) if UpsertOnPlaybackStart was never called
        // for this profile/canonicalKey - progress can only be recorded
        // against an entry that already exists.
        public WatchStateEntry RecordProgress(
            Guid profileId,
            string canonicalKey,
            double positionSeconds,
            double durationSeconds,
            bool reachedEndOfFile)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return null;
            }

            WatchStateEntry entry = FindEntry(profileId, canonicalKey);

            if (entry == null)
            {
                return null;
            }

            entry.PositionSeconds = Math.Max(0, positionSeconds);

            if (durationSeconds > 0)
            {
                entry.DurationSeconds = durationSeconds;
            }

            entry.LastPlayedUtc = DateTime.UtcNow;

            bool completedByFraction =
                entry.DurationSeconds > 0 &&
                (entry.PositionSeconds / entry.DurationSeconds) >= CompletionFraction;

            // Completion is one-directional here - once true, a later
            // progress tick reporting an earlier position (e.g. the user
            // rewound after finishing) must not flip it back to false;
            // only a fresh UpsertOnPlaybackStart (a deliberate rewatch)
            // does that, by design, above.
            if (reachedEndOfFile || completedByFraction)
            {
                entry.IsCompleted = true;
            }

            TryPersist();

            return entry;
        }

        // =========================================================
        // PROFILE DELETION CLEANUP
        // =========================================================
        //
        // Wired to ProfileService.ProfileDeleted by whoever constructs
        // both services together (see StremioVideoCenter.cs) - "a profile
        // owns all personal media state" means deleting a profile must
        // also delete its Watch State, never leave it orphaned.
        public void RemoveAllForProfile(Guid profileId)
        {
            int removed = _settings.WatchStates.RemoveAll(
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
        // "Maximum 500 Watch State entries per profile. If the cap is
        // exceeded, remove the oldest appropriate entries using
        // LastPlayedUtc. Be careful not to remove active/incomplete
        // state unnecessarily." (Slice 16B spec, Section 12). Kept
        // simple and deterministic per the spec's own instruction:
        // completed entries are the eviction pool first (oldest
        // LastPlayedUtc first); only if still over the cap after
        // removing every completed entry does eviction reach into
        // incomplete entries (again oldest first) - an incomplete entry
        // represents state a future resume feature still needs, so it is
        // the last thing removed, not the first.
        private void EnforceRetentionCap(Guid profileId)
        {
            List<WatchStateEntry> profileEntries = _settings.WatchStates
                .Where(entry => entry != null && entry.ProfileId == profileId)
                .ToList();

            int overflow = profileEntries.Count - MaxEntriesPerProfile;

            if (overflow <= 0)
            {
                return;
            }

            IEnumerable<WatchStateEntry> evictionOrder = profileEntries
                .OrderBy(entry => entry.IsCompleted ? 0 : 1)
                .ThenBy(entry => entry.LastPlayedUtc);

            foreach (WatchStateEntry entry in evictionOrder.Take(overflow))
            {
                _settings.WatchStates.Remove(entry);
            }
        }

        // =========================================================
        // LOOKUP
        // =========================================================

        private WatchStateEntry FindEntry(Guid profileId, string canonicalKey)
        {
            return _settings.WatchStates.FirstOrDefault(entry =>
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
                // Persistence must never crash the caller mid-playback -
                // the in-memory WatchStates state above has already been
                // updated regardless, mirroring ProfileService.TryPersist
                // exactly.
                Playnite.SDK.LogManager.GetLogger().Warn(
                    ex,
                    "WatchStateService: failed to persist watch state settings.");
            }
        }
    }
}
