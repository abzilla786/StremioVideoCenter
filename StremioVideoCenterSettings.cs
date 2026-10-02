using Playnite.SDK;
using Playnite.SDK.Data;
using StremioVideoCenter.Models;
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
        private string stremioAddonUrls = "http://127.0.0.1:7000";
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

        public const int CurrentSettingsSchemaVersion = 1;

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
            }

            AddonInstances.Add(newInstance);
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