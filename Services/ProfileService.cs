using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // PROFILE SERVICE (Slice 16A - Profile Foundation)
    // =============================================================
    //
    // Owns StremioVideoCenterSettings.Profiles/ActiveProfileId end to
    // end - the single, well-defined owner of this persisted state,
    // mirroring exactly how LocalLibraryService is the only class that
    // touches LibraryLocations. Every mutation here (create/rename/
    // switch) is immediately followed by a persist call, because unlike
    // AddonInstances/LibraryLocations (which are only ever edited from
    // Playnite's own Settings dialog, with its own BeginEdit/EndEdit
    // Save/Cancel lifecycle), profile changes happen live, inside the
    // already-open Video Center window, and must survive a later crash
    // or an abrupt Playnite exit without the user ever visiting
    // Settings - see StremioVideoCenter.cs for how the save delegate
    // passed into the constructor is wired to the plugin's own
    // SavePluginSettings.
    //
    // "A profile owns all personal media state" (Slice 16 architecture
    // decisions): this class is deliberately limited to the profile
    // records themselves and which one is active. Watch State/Continue
    // Watching/History/My List (Slice 16B onward) are separate services
    // that each take a ProfileId, not something ProfileService knows
    // about - keeping this class focused exactly the same way
    // LocalLibraryService stays free of scanning/identification concerns.
    public class ProfileService
    {
        // "Maximum 5 profiles" (Slice 16 spec, Section 2).
        public const int MaxProfiles = 5;

        private const string DefaultProfileName = "Profile 1";

        private readonly StremioVideoCenterSettings _settings;
        private readonly Action _persist;

        // persist may be null (e.g. a design-time/test caller) - every
        // call site below already tolerates that via TryPersist, so a
        // null delegate degrades to "in-memory only for this session"
        // rather than throwing.
        public ProfileService(StremioVideoCenterSettings settings, Action persist)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;
            _persist = persist;

            if (_settings.Profiles == null)
            {
                _settings.Profiles = new List<UserProfile>();
            }

            EnsureAtLeastOneProfile();
        }

        // =========================================================
        // ENSURE AT LEAST ONE PROFILE
        // =========================================================
        //
        // "There must never be a valid application state with zero
        // profiles" (Slice 16 spec, Section 3) - enforced here, at
        // construction, so this invariant holds the moment ProfileService
        // exists, independent of whether the Profile Selection UI has
        // even been shown yet. Also repairs an ActiveProfileId that no
        // longer points at a real profile (e.g. settings hand-edited, or
        // a future edge case) by falling back to the first profile -
        // never leaves ActiveProfileId dangling.
        private void EnsureAtLeastOneProfile()
        {
            bool changed = false;

            if (_settings.Profiles.Count == 0)
            {
                DateTime now = DateTime.UtcNow;

                _settings.Profiles.Add(new UserProfile
                {
                    Id = Guid.NewGuid(),
                    Name = DefaultProfileName,
                    ColorKey = ProfileColorPalette.Colors[0],
                    CreatedUtc = now,
                    LastActiveUtc = now
                });

                changed = true;
            }

            bool activeIsValid =
                !string.IsNullOrWhiteSpace(_settings.ActiveProfileId) &&
                _settings.Profiles.Any(profile =>
                    profile != null &&
                    string.Equals(
                        profile.Id.ToString(),
                        _settings.ActiveProfileId,
                        StringComparison.OrdinalIgnoreCase));

            if (!activeIsValid)
            {
                _settings.ActiveProfileId = _settings.Profiles[0].Id.ToString();
                changed = true;
            }

            if (changed)
            {
                TryPersist();
            }
        }

        // =========================================================
        // LIST
        // =========================================================

        public IReadOnlyList<UserProfile> GetProfiles()
        {
            return _settings.Profiles.ToList();
        }

        public bool CanCreateProfile()
        {
            return _settings.Profiles.Count < MaxProfiles;
        }

        // "The final remaining profile MUST NOT be deletable" (16A fix
        // spec, Section 2) - the same "never zero profiles" invariant
        // EnsureAtLeastOneProfile already enforces at construction, just
        // exposed here so ProfileSelectionView can hide/disable its own
        // Delete affordance without duplicating the rule.
        public bool CanDeleteProfile()
        {
            return _settings.Profiles.Count > 1;
        }

        // =========================================================
        // ACTIVE PROFILE
        // =========================================================

        public UserProfile GetActiveProfile()
        {
            UserProfile match = _settings.Profiles.FirstOrDefault(profile =>
                profile != null &&
                string.Equals(
                    profile.Id.ToString(),
                    _settings.ActiveProfileId,
                    StringComparison.OrdinalIgnoreCase));

            // EnsureAtLeastOneProfile already guarantees this is only
            // reached if something mutated Profiles/ActiveProfileId after
            // construction without going through this class - fall back
            // defensively rather than returning null to a caller that
            // (per the "never zero profiles" invariant) should never have
            // to null-check this.
            return match ?? _settings.Profiles.FirstOrDefault();
        }

        public Guid? GetActiveProfileId()
        {
            UserProfile active = GetActiveProfile();
            return active?.Id;
        }

        // Returns false (no-op) if profileId does not match an existing
        // profile - callers should treat that as "selection failed" and
        // leave the previous active profile in place rather than clear
        // it.
        public bool SetActiveProfile(Guid profileId)
        {
            UserProfile match = _settings.Profiles.FirstOrDefault(profile =>
                profile != null && profile.Id == profileId);

            if (match == null)
            {
                return false;
            }

            _settings.ActiveProfileId = match.Id.ToString();
            match.LastActiveUtc = DateTime.UtcNow;

            TryPersist();

            return true;
        }

        // =========================================================
        // CREATE
        // =========================================================
        //
        // Returns null if the 5-profile limit has already been reached
        // or name is blank - callers (ProfileSelectionView) are expected
        // to have already hidden/disabled the "Add Profile" affordance
        // per CanCreateProfile() above, but this is enforced here too so
        // the limit can never be bypassed by any other future caller.
        public UserProfile CreateProfile(string name)
        {
            string trimmedName = name?.Trim();

            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                return null;
            }

            if (!CanCreateProfile())
            {
                return null;
            }

            DateTime now = DateTime.UtcNow;

            var profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                Name = trimmedName,
                ColorKey = NextColorKey(),
                CreatedUtc = now,
                LastActiveUtc = now
            };

            _settings.Profiles.Add(profile);

            TryPersist();

            return profile;
        }

        // =========================================================
        // RENAME
        // =========================================================
        //
        // "Renaming a profile must NOT break, reset or orphan any
        // existing data" (Slice 16 spec, Section 2) - Id is never
        // touched here, only Name, so anything keyed by ProfileId (Watch
        // State/History/My List, Slice 16B onward) is unaffected by a
        // rename.
        public bool RenameProfile(Guid profileId, string newName)
        {
            string trimmedName = newName?.Trim();

            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                return false;
            }

            UserProfile match = _settings.Profiles.FirstOrDefault(profile =>
                profile != null && profile.Id == profileId);

            if (match == null)
            {
                return false;
            }

            match.Name = trimmedName;

            TryPersist();

            return true;
        }

        // =========================================================
        // DELETE (16A fix #2 - profile deletion)
        // =========================================================
        //
        // Returns false (no-op) if profileId does not match an existing
        // profile, or if this would remove the last remaining profile -
        // CanDeleteProfile() above is expected to have already hidden the
        // Delete affordance in that case, but this is enforced here too so
        // the invariant can never be bypassed by any other future caller
        // (mirrors how CreateProfile() re-checks CanCreateProfile() itself).
        //
        // ProfileDeleted is raised BEFORE the profile record itself is
        // removed, carrying the still-valid profileId, specifically so a
        // future Slice 16B+ service (Watch State/Continue Watching/
        // History/My List) can subscribe and clean up its own
        // ProfileId-scoped data without ProfileService ever needing to
        // know those systems exist - "design profile deletion so that
        // future profile-owned data can be removed cleanly" (16A fix spec)
        // without actually implementing any of those systems now.
        public event EventHandler<Guid> ProfileDeleted;

        public bool DeleteProfile(Guid profileId)
        {
            if (!CanDeleteProfile())
            {
                return false;
            }

            UserProfile match = _settings.Profiles.FirstOrDefault(profile =>
                profile != null && profile.Id == profileId);

            if (match == null)
            {
                return false;
            }

            ProfileDeleted?.Invoke(this, profileId);

            _settings.Profiles.Remove(match);

            // Reuses the exact same repair logic construction already
            // relies on for a dangling/invalid ActiveProfileId - "never
            // leave ActiveProfileId pointing at a deleted profile" (16A fix
            // spec) is the same invariant EnsureAtLeastOneProfile already
            // guarantees, so there is no separate repair path to keep in
            // sync here.
            EnsureAtLeastOneProfile();

            TryPersist();

            return true;
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
                // Persistence must never crash the caller mid-profile-
                // operation - the in-memory Profiles/ActiveProfileId
                // state above has already been updated regardless, so
                // the running session still behaves correctly even if a
                // save genuinely failed; it will simply be retried on the
                // next mutation (or lost on an unexpected process exit,
                // same "best-effort settings save" behaviour Playnite's
                // own plugin-settings system already has everywhere
                // else in this codebase).
                Playnite.SDK.LogManager.GetLogger().Warn(
                    "ProfileService: failed to persist profile settings. " +
                    UserFacingErrors.ForLog(ex));
            }
        }

        private string NextColorKey()
        {
            int usedCount = _settings.Profiles.Count;
            return ProfileColorPalette.Colors[usedCount % ProfileColorPalette.Colors.Length];
        }
    }

    // =========================================================
    // PROFILE COLOUR PALETTE
    // =========================================================
    //
    // A small fixed set of built-in avatar colours - deliberately not a
    // general theming system, just enough to make up to 5 profile tiles
    // visually distinct without an image-upload pipeline (see
    // UserProfile.ColorKey remarks).
    public static class ProfileColorPalette
    {
        public static readonly string[] Colors =
        {
            "#E91E63",
            "#3F51B5",
            "#009688",
            "#FF9800",
            "#8E24AA"
        };
    }
}
