using Playnite.SDK;
using Playnite.SDK.Data;
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
        private string tmdbApiKey = string.Empty;
        private string omdbApiKey = string.Empty;
        private string realDebridApiKey = string.Empty;
        private string stremioAddonUrls = "http://127.0.0.1:7000";

        // =========================================================
        // TMDB API CREDENTIAL
        // =========================================================

        public string TmdbApiKey
        {
            get => tmdbApiKey;
            set => SetValue(ref tmdbApiKey, value);
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
            // user has configured TMDB or OMDb.
            return true;
        }
    }
}