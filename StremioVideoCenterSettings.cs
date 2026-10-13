using Playnite.SDK;
using Playnite.SDK.Data;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;

namespace StremioVideoCenter
{
    // =========================================================
    // STORED PLUGIN SETTINGS
    // =========================================================
    //
    // These values are saved by Playnite's plugin settings system.
    // No real API credentials are included in the source code.
    //
    public class StremioVideoCenterSettings : ObservableObject
    {
        private int settingsSchemaVersion;
        private string omdbApiKey = string.Empty;
        private string realDebridApiKey = string.Empty;
        private string stremioAddonUrls = string.Empty;
        private List<AddonInstance> addonInstances = new List<AddonInstance>();
        private List<string> seededCoreAddonProviderKeys = new List<string>();
        private List<LibraryLocation> libraryLocations = new List<LibraryLocation>();
        private List<UserProfile> profiles = new List<UserProfile>();
        private string activeProfileId = string.Empty;
        private List<WatchStateEntry> watchStates = new List<WatchStateEntry>();
        private List<WatchHistoryEntry> watchHistory = new List<WatchHistoryEntry>();
        private List<MyListEntry> myListEntries = new List<MyListEntry>();

        // =========================================================
        // SETTINGS SCHEMA VERSION
        // =========================================================
        //
        // Persisted marker for one-time settings clean-ups. Version 1 =
        // the Slice 17D TMDB removal: the TMDB credential property no
        // longer exists, so a config.json saved by an older build still
        // carries an orphaned "TmdbApiKey" value until the settings are
        // saved again. StremioVideoCenterSettingsViewModel's constructor
        // saves once when this is below CurrentSettingsSchemaVersion,
        // which rewrites config.json (via the normal SavePluginSettings
        // path) without the obsolete field.

        // Version 2 = Slice 20 removal of the obsolete 7000 legacy entry
        // (see RemoveObsoleteLoopbackAddons below). Version 3 = Slice 20
        // correction: Local Files becomes a built-in registration with no
        // address (see NormaliseLocalFilesRegistration below).

        public const int CurrentSettingsSchemaVersion = 3;

        public int SettingsSchemaVersion
        {
            get => settingsSchemaVersion;
            set => SetValue(ref settingsSchemaVersion, value);
        }

        // =========================================================
        // OMDB API CREDENTIAL
        // =========================================================

        public string OmdbApiKey
        {
            get => omdbApiKey;
            set => SetValue(ref omdbApiKey, value);
        }

        // =========================================================
        // REAL-DEBRID API CREDENTIAL
        // =========================================================

        public string RealDebridApiKey
        {
            get => realDebridApiKey;
            set => SetValue(ref realDebridApiKey, value);
        }

        // =========================================================
        // STREMIO ADDON URLS (newline-delimited, one per line)
        // =========================================================

        public string StremioAddonUrls
        {
            get => stremioAddonUrls;
            set => SetValue(ref stremioAddonUrls, value);
        }

        // =========================================================
        // STRUCTURED ADDON INSTANCES
        // =========================================================
        //
        // Each entry is one configured Stremio addon (a manifest URL
        // plus display/category metadata). This is additive to
        // StremioAddonUrls above, not a replacement - see
        // SyncLegacyAddonUrls for how the two stay in sync.

        public List<AddonInstance> AddonInstances
        {
            get => addonInstances;
            set => SetValue(ref addonInstances, value);
        }

        // =========================================================
        // CORE ADDON SEEDING MEMORY
        // =========================================================
        //
        // Records which core-addon ProviderKeys have EVER been
        // successfully seeded (see CoreAddonSeedingService), independent
        // of whether that instance still exists in AddonInstances today.
        // This is what makes seeding idempotent AND respectful of user
        // removal: a key is added here only once seeding actually
        // succeeded, and is never removed just because the user later
        // deletes that instance - so it is never silently recreated on a
        // later launch. A key that failed to seed (e.g. no network on
        // first run) is intentionally left OFF this list so seeding can
        // retry on the next launch, since "core addons actually
        // installed out of the box" is a hard shipping requirement.
        public List<string> SeededCoreAddonProviderKeys
        {
            get => seededCoreAddonProviderKeys;
            set => SetValue(ref seededCoreAddonProviderKeys, value);
        }

        // =========================================================
        // LOCAL MEDIA LIBRARY LOCATIONS (Slice 15 - architecture only)
        // =========================================================
        //
        // The Local Media / File Manager architecture's persisted state -
        // just the list of folders the user has added as local media
        // sources. Reuses this exact Playnite plugin-settings mechanism
        // (the same one AddonInstances already uses) rather than a new
        // file or database - see LocalLibraryService, which is the only
        // class that reads/writes this list. No scan RESULTS are
        // persisted here, only the locations themselves - scanning is
        // on-demand this slice (see LocalMediaScanService remarks).
        public List<LibraryLocation> LibraryLocations
        {
            get => libraryLocations;
            set => SetValue(ref libraryLocations, value);
        }

        // =========================================================
        // LOCAL PROFILES (Slice 16A - Profile Foundation)
        // =========================================================
        //
        // Local "who's watching" profiles - not authentication/security
        // accounts (see Models.UserProfile remarks). Reuses this exact
        // Playnite plugin-settings mechanism, the same one AddonInstances/
        // LibraryLocations already use. The sole owner of this list and
        // of ActiveProfileId below is Services.ProfileService - mirrors
        // how LibraryLocations is only ever touched through
        // LocalLibraryService.
        public List<UserProfile> Profiles
        {
            get => profiles;
            set => SetValue(ref profiles, value);
        }

        // The Guid (as a string) of the profile currently in use. Kept as
        // a plain string, not a Guid, for the same reason every other
        // persisted identifier in this codebase is a string (Playnite's
        // JSON settings serialization) - ProfileService is responsible
        // for ever reading this as a Guid.
        public string ActiveProfileId
        {
            get => activeProfileId;
            set => SetValue(ref activeProfileId, value);
        }

        // =========================================================
        // PROFILE-OWNED WATCH STATE (Slice 16B)
        // =========================================================
        //
        // Every persisted playback-progress record, across every
        // profile - reuses this exact Playnite plugin-settings
        // mechanism, the same one Profiles/AddonInstances/
        // LibraryLocations already use, rather than a separate database
        // (Slice 16B spec, Section 7). The sole owner of this list is
        // Services.WatchStateService - mirrors how Profiles is only ever
        // touched through ProfileService. Profile isolation (Section 8)
        // is enforced by WatchStateService filtering every read/write by
        // ProfileId, not by anything here.
        public List<WatchStateEntry> WatchStates
        {
            get => watchStates;
            set => SetValue(ref watchStates, value);
        }

        // =========================================================
        // PROFILE-OWNED WATCH HISTORY (Slice 16C)
        // =========================================================
        //
        // Every persisted series/movie-level History record, across
        // every profile - same reuse-the-existing-mechanism reasoning as
        // WatchStates above. The sole owner is Services.WatchHistoryService.
        public List<WatchHistoryEntry> WatchHistory
        {
            get => watchHistory;
            set => SetValue(ref watchHistory, value);
        }

        // =========================================================
        // PROFILE-OWNED MY LIST (Slice 17A)
        // =========================================================
        //
        // Every persisted My List (content-level "saved for later")
        // record, across every profile - same reuse-the-existing-
        // mechanism reasoning as WatchStates/WatchHistory above, rather
        // than a separate database. The sole owner is
        // Services.MyListService - mirrors how WatchHistory is only ever
        // touched through WatchHistoryService. Profile isolation is
        // enforced by MyListService filtering every read/write by
        // ProfileId, not by anything here.
        public List<MyListEntry> MyListEntries
        {
            get => myListEntries;
            set => SetValue(ref myListEntries, value);
        }

        // =========================================================
        // INSTALL OR REPLACE ADDON INSTANCE
        // =========================================================
        //
        // Single entry point for adding a newly-fetched-and-validated
        // AddonInstance to AddonInstances, honouring InstallPolicy:
        //
        //   ReplaceExisting (Torrentio/Comet): every existing instance
        //   sharing the new instance's ProviderKey is removed, then the
        //   new instance is added - so exactly one active instance per
        //   ProviderKey ever exists. Callers must only invoke this AFTER
        //   the new instance's manifest has already been fetched and
        //   validated (see StremioVideoCenterSettingsView.
        //   InstallAddonFromManifestUrlAsync) - this method itself does
        //   no fetching, so the "never delete the old config first"
        //   safety property comes from ordering at the call site, not
        //   from anything here: by the time this runs, the replacement
        //   is already known-good.
        //
        //   AllowMultiple (generic custom addons): unchanged prior
        //   behaviour - simply appended, no removal.
        //
        // Never partially applies - the list is only mutated once, via
        // Add/RemoveAll below, both of which are synchronous and cannot
        // leave AddonInstances in an inconsistent in-between state.
        public void InstallOrReplaceAddonInstance(AddonInstance newInstance)
        {
            if (newInstance == null)
            {
                return;
            }

            if (AddonInstances == null)
            {
                AddonInstances = new List<AddonInstance>();
            }

            bool isReplace =
                string.Equals(
                    newInstance.InstallPolicy,
                    AddonInstallPolicy.ReplaceExisting,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(newInstance.ProviderKey);

            if (isReplace)
            {
                AddonInstances.RemoveAll(existing =>
                    existing != null &&
                    string.Equals(
                        existing.ProviderKey,
                        newInstance.ProviderKey,
                        StringComparison.OrdinalIgnoreCase));

                // An instance of the SAME addon (same manifest id) that was
                // installed earlier through "Other Addons" has no
                // ProviderKey, so the match above misses it and a second
                // copy would be queried. Replace it too. Instances that
                // carry any ProviderKey (including the core addons) are
                // never touched by this.
                if (!string.IsNullOrWhiteSpace(newInstance.AddonId))
                {
                    // Before they are dropped: carry over the Enabled state
                    // (only that - everything else comes from the freshly
                    // fetched manifest) and remove each replaced instance's
                    // own URL from the legacy StremioAddonUrls, so
                    // SyncLegacyAddonUrls cannot re-create it as a second
                    // "Legacy" copy. Other lines are untouched.
                    bool carriedEnabled = false;

                    foreach (AddonInstance replaced in AddonInstances.FindAll(existing =>
                        existing != null &&
                        string.IsNullOrWhiteSpace(existing.ProviderKey) &&
                        string.Equals(
                            existing.AddonId,
                            newInstance.AddonId,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!carriedEnabled)
                        {
                            newInstance.Enabled = replaced.Enabled;
                            carriedEnabled = true;
                        }

                        RemoveLegacyUrl(replaced.ManifestUrl);
                    }

                    AddonInstances.RemoveAll(existing =>
                        existing != null &&
                        string.IsNullOrWhiteSpace(existing.ProviderKey) &&
                        string.Equals(
                            existing.AddonId,
                            newInstance.AddonId,
                            StringComparison.OrdinalIgnoreCase));
                }
            }

            AddonInstances.Add(newInstance);
        }

        // =========================================================
        // REMOVE A LEGACY URL WHEN ITS ADDON IS REMOVED
        // =========================================================
        //
        // SyncLegacyAddonUrls re-adds every StremioAddonUrls line that has
        // no matching AddonInstance, so removing only the instance made a
        // legacy addon come back on the next load/Save. Removing the
        // matching saved line (case-insensitive, trimmed) with the
        // instance keeps the removal. Lines that do not match are kept
        // exactly as they were. Returns true if a line was removed.
        public bool RemoveLegacyUrl(string manifestUrl)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl) ||
                string.IsNullOrWhiteSpace(StremioAddonUrls))
            {
                return false;
            }

            string target = manifestUrl.Trim();
            string[] lines = StremioAddonUrls.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            var kept = new List<string>();
            bool removed = false;

            foreach (string line in lines)
            {
                if (string.Equals(line.Trim(), target, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }

                kept.Add(line);
            }

            if (removed)
            {
                StremioAddonUrls = string.Join("\n", kept);
            }

            return removed;
        }

        // =========================================================
        // ONE-TIME REMOVAL OF OBSOLETE LOOPBACK ADDON ENTRIES
        // =========================================================
        //
        // Slice 20. Targeted, version-gated (SettingsSchemaVersion < 2)
        // removal of http://127.0.0.1:7000, the old default legacy URL:
        // its "Legacy" AddonInstance and its StremioAddonUrls line,
        // matched by exact loopback host + port only. Every other
        // instance and URL - including other localhost addons - is left
        // alone. Must run BEFORE SyncLegacyAddonUrls. (The obsolete
        // 11470 Local Files endpoint is handled separately by
        // NormaliseLocalFilesRegistration.) Returns true if anything
        // changed.
        public bool RemoveObsoleteLoopbackAddons()
        {
            bool changed = false;

            if (AddonInstances != null)
            {
                int before = AddonInstances.Count;

                AddonInstances.RemoveAll(instance =>
                    instance != null &&
                    IsLoopbackEndpoint(instance.ManifestUrl, 7000) &&
                    string.Equals(instance.Category, "Legacy", StringComparison.OrdinalIgnoreCase));

                changed = AddonInstances.Count != before;
            }

            if (!string.IsNullOrWhiteSpace(StremioAddonUrls))
            {
                string[] lines = StremioAddonUrls.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);

                var kept = new List<string>();

                foreach (string line in lines)
                {
                    if (IsLoopbackEndpoint(line, 7000))
                    {
                        changed = true;
                        continue;
                    }

                    kept.Add(line);
                }

                if (kept.Count != lines.Length)
                {
                    StremioAddonUrls = string.Join("\n", kept);
                }
            }

            return changed;
        }

        // =========================================================
        // LOCAL FILES: BUILT-IN REGISTRATION (version 3)
        // =========================================================
        //
        // Slice 20 correction. Local Files is a built-in registration
        // with no addon address (CoreAddonSeedingService.
        // CreateLocalFilesDefinition); the obsolete http://127.0.0.1:11470
        // endpoint must not come back. Version-gated
        // (SettingsSchemaVersion < 3), so it runs once:
        //   - an existing "localfiles" instance still pointing at 11470
        //     is replaced in place by the built-in definition (keeping
        //     its Enabled choice and its position in the list);
        //   - an existing "localfiles" instance with any other address
        //     is left alone;
        //   - no "localfiles" instance, but "localfiles" was seeded
        //     before (the 11470 entry was removed by the earlier version 2
        //     migration or by the user): the built-in definition is added;
        //   - never seeded (fresh install): nothing here - normal core
        //     seeding adds it;
        //   - any OTHER instance at the 11470 endpoint is removed.
        // Must run BEFORE CoreAddonSeedingService.SeedIfNeeded.
        // Returns true if anything changed.
        public bool NormaliseLocalFilesRegistration()
        {
            if (AddonInstances == null)
            {
                AddonInstances = new List<AddonInstance>();
            }

            bool changed = false;

            int removed = AddonInstances.RemoveAll(instance =>
                instance != null &&
                !IsLocalFilesInstance(instance) &&
                IsLoopbackEndpoint(instance.ManifestUrl, 11470));

            if (removed > 0)
            {
                changed = true;
            }

            int index = AddonInstances.FindIndex(IsLocalFilesInstance);

            if (index >= 0)
            {
                AddonInstance existing = AddonInstances[index];

                if (IsLoopbackEndpoint(existing.ManifestUrl, 11470))
                {
                    AddonInstance builtIn = CoreAddonSeedingService.CreateLocalFilesDefinition();
                    builtIn.Enabled = existing.Enabled;
                    AddonInstances[index] = builtIn;
                    changed = true;
                }
            }
            else if (SeededCoreAddonProviderKeys != null &&
                     SeededCoreAddonProviderKeys.Exists(key =>
                        string.Equals(key, "localfiles", StringComparison.OrdinalIgnoreCase)))
            {
                AddonInstances.Add(CoreAddonSeedingService.CreateLocalFilesDefinition());
                changed = true;
            }

            return changed;
        }

        private static bool IsLocalFilesInstance(AddonInstance instance)
        {
            return instance != null &&
                   string.Equals(instance.ProviderKey, "localfiles", StringComparison.OrdinalIgnoreCase);
        }

        // True only for http://127.0.0.1:<port> (or localhost), with no
        // path other than "/" or "/manifest.json" or the seeded
        // "/local-addon", and no query string.
        private static bool IsLoopbackEndpoint(string url, int port)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            Uri uri;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                uri.Port != port ||
                !string.IsNullOrEmpty(uri.Query))
            {
                return false;
            }

            if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) &&
                !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string path = uri.AbsolutePath.TrimEnd('/');

            return path.Length == 0 ||
                   string.Equals(path, "/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "/local-addon", StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // MIGRATE LEGACY ADDON URLS INTO ADDON INSTANCES
        // =========================================================
        //
        // Backward-compatible, additive, one-directional migration:
        // every URL already present in the legacy StremioAddonUrls
        // setting that is not yet represented in AddonInstances (by
        // case-insensitive ManifestUrl match) is added as a new
        // "Legacy" category instance. StremioAddonUrls itself is never
        // cleared or modified by this - both settings keep working
        // side by side. Safe to call repeatedly (idempotent).
        public void SyncLegacyAddonUrls()
        {
            if (AddonInstances == null)
            {
                AddonInstances = new List<AddonInstance>();
            }

            if (string.IsNullOrWhiteSpace(StremioAddonUrls))
            {
                return;
            }

            string[] lines = StremioAddonUrls.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                bool alreadyPresent = AddonInstances.Exists(instance =>
                    instance != null &&
                    string.Equals(
                        instance.ManifestUrl,
                        trimmed,
                        StringComparison.OrdinalIgnoreCase));

                if (alreadyPresent)
                {
                    continue;
                }

                AddonInstances.Add(new AddonInstance
                {
                    FriendlyName = trimmed,
                    ManifestUrl = trimmed,
                    Enabled = true,
                    Category = "Legacy"
                });
            }
        }
    }

    // =========================================================
    // SETTINGS VIEW MODEL
    // =========================================================

    public class StremioVideoCenterSettingsViewModel
        : ObservableObject, ISettings
    {
        private readonly StremioVideoCenter plugin;

        private StremioVideoCenterSettings editingClone { get; set; }

        private StremioVideoCenterSettings settings;

        public StremioVideoCenterSettings Settings
        {
            get => settings;

            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterSettingsViewModel(
            StremioVideoCenter plugin)
        {
            // Playnite requires the plugin instance so that
            // settings are saved to this plugin's storage location.
            this.plugin = plugin;

            // Load previously saved settings.
            var savedSettings =
                plugin.LoadPluginSettings<StremioVideoCenterSettings>();

            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                // First launch - create default settings.
                Settings = new StremioVideoCenterSettings();
            }

            // One-directional, additive, idempotent migration - any
            // legacy StremioAddonUrls entry not yet represented as an
            // AddonInstance is added as one. Runs on every load so
            // settings saved by an older build still pick up their
            // instance the first time this Settings view is opened.
            // Slice 20: one-time removal of the stale 7000/11470 loopback
            // entries. Version-gated and run BEFORE the sync below so the
            // sync cannot re-create the 7000 instance from its URL line.
            if (Settings.SettingsSchemaVersion < 2)
            {
                Settings.RemoveObsoleteLoopbackAddons();
            }

            // Slice 20 correction: Local Files becomes a built-in
            // registration with no address (replaces the obsolete 11470
            // entry; restores the entry the version 2 migration removed).
            // Runs before core seeding below.
            if (Settings.SettingsSchemaVersion < 3)
            {
                Settings.NormaliseLocalFilesRegistration();
            }

            Settings.SyncLegacyAddonUrls();

            // One-time clean-up of settings saved by builds that still had
            // the TMDB credential (removed in Slice 17D): the old
            // "TmdbApiKey" value is ignored on load, and this single save
            // through the normal persistence path rewrites config.json
            // without it. The version marker stops it repeating.
            if (Settings.SettingsSchemaVersion < StremioVideoCenterSettings.CurrentSettingsSchemaVersion)
            {
                Settings.SettingsSchemaVersion = StremioVideoCenterSettings.CurrentSettingsSchemaVersion;

                try
                {
                    plugin.SavePluginSettings(Settings);
                }
                catch (Exception ex)
                {
                    Playnite.SDK.LogManager.GetLogger().Warn(
                        ex,
                        "Settings clean-up save failed; will retry on next launch.");
                }
            }

            // Fresh-install / core-addon seeding. Runs here (the plugin
            // constructor's ISettings load, not just when the Settings
            // UI happens to be opened) so a fresh install actually has
            // Cinemeta/AnimeFLV/OpenSubtitles v3/Local Files registered
            // the first time the user opens Video Center, not only after
            // they visit Settings. Idempotent - see
            // CoreAddonSeedingService and Settings.
            // SeededCoreAddonProviderKeys for how repeat launches and
            // user-removal are both respected. Only persists if seeding
            // actually changed anything, so a normal launch with
            // everything already seeded does not rewrite the settings
            // file for no reason.
            try
            {
                bool seeded =
                    Services.CoreAddonSeedingService.SeedIfNeeded(Settings);

                if (seeded)
                {
                    plugin.SavePluginSettings(Settings);
                }
            }
            catch (Exception ex)
            {
                // Seeding must never prevent the plugin from loading -
                // Settings.AddonInstances is left exactly as it was
                // before this attempt (SeedIfNeeded only mutates the
                // list after each addon's own construction succeeds),
                // and the next launch will simply retry.
                Playnite.SDK.LogManager.GetLogger().Warn(
                    ex,
                    "Core addon seeding failed; will retry on next launch.");
            }
        }

        // =========================================================
        // BEGIN EDIT
        // =========================================================

        public void BeginEdit()
        {
            // Create a clone so Cancel can restore the
            // settings that existed before editing began.
            editingClone =
                Serialization.GetClone(Settings);
        }

        // =========================================================
        // CANCEL EDIT
        // =========================================================

        public void CancelEdit()
        {
            // Restore the settings from before editing started.
            Settings = editingClone;
        }

        // =========================================================
        // SAVE SETTINGS
        // =========================================================

        public void EndEdit()
        {
            // Re-run the legacy-URL migration before saving in case the
            // user edited StremioAddonUrls directly during this session.
            Settings.SyncLegacyAddonUrls();

            // Save the current settings using Playnite's
            // standard plugin settings system.
            plugin.SavePluginSettings(Settings);
        }

        // =========================================================
        // VALIDATE SETTINGS
        // =========================================================

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            // API credentials are optional at this stage.
            //
            // This allows the plugin to open even before the
            // user has configured OMDb.
            return true;
        }
    }
}