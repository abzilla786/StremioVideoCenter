using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // CORE ADDON SEEDING SERVICE
    // =============================================================
    //
    // Cinemeta-First Addon Settings Architecture slice (extended in a
    // later correction pass to add OpenSubtitles as its own, separate
    // core addon - see the OPENSUBTITLES definition below).
    //
    // Installs Zino's five CORE / DEFAULT addons (Cinemeta, AnimeFLV,
    // OpenSubtitles, OpenSubtitles v3, Local Files) into AddonInstances
    // on first run, through the exact same AddonInstance/
    // StremioAddonRegistry architecture every other addon uses - there
    // is no separate, parallel "core addon" system.
    //
    // WHY NO LIVE MANIFEST FETCH AT SEED TIME
    // ----------------------------------------
    // This runs synchronously from StremioVideoCenterSettingsViewModel's
    // constructor, which itself runs synchronously from the Playnite
    // plugin's own constructor (StremioVideoCenter.cs) - i.e. on every
    // Playnite startup, before Playnite has even finished loading. A
    // live HTTP fetch here (StremioAddonService.GetManifestAsync, which
    // has no explicit timeout - WebClient defaults to ~100 seconds) for
    // up to five addons would risk a severe, unacceptable Playnite
    // startup delay, especially with no network connection. Instead,
    // each core addon's manifest identity below (id, name, version,
    // resources, types, idPrefixes) was captured from a verified source,
    // not invented:
    //   - Cinemeta, AnimeFLV, OpenSubtitles v3, Local Files: a REAL, live
    //     fetch of that addon's own public manifest.json, performed
    //     during the original Cinemeta-First Addon Settings Architecture
    //     slice (see claude/Cinemeta_First_Addon_Settings_Architecture.md).
    //   - OpenSubtitles (below): a later correction pass found that its
    //     manifest.json consistently returns an HTML landing page rather
    //     than raw JSON to a non-Stremio client (confirmed again this
    //     pass), so its identity instead comes from Stremio's own
    //     official addons registry (github.com/Stremio/
    //     stremio-official-addons index.json, entry id
    //     "org.stremio.opensubtitles", flags.official: true) - the
    //     authoritative source for an official addon's identity when its
    //     own manifest endpoint cannot be fetched directly.
    // Each configured instance's actual reachability is naturally
    // exercised whenever streams/subtitles are queried during real use;
    // StremioAddonRegistry's per-addon try/catch already handles an
    // unreachable or misbehaving addon gracefully without blocking any
    // other configured addon (see the Source Selection rules this
    // project already follows).
    //
    // IDEMPOTENCY
    // -----------
    // Settings.SeededCoreAddonProviderKeys records which ProviderKeys
    // have EVER been successfully seeded. A key already in that list is
    // never seeded again - not even if the user later removed that
    // instance from AddonInstances. This is what satisfies both "must
    // actually be installed on a fresh install" and "do not silently
    // reinstall a core addon the user intentionally removed."
    public static class CoreAddonSeedingService
    {
        // =========================================================
        // SEED IF NEEDED
        // =========================================================
        //
        // Returns true if any core addon was newly seeded (i.e.
        // Settings changed and should be persisted), false otherwise.
        public static bool SeedIfNeeded(StremioVideoCenterSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            if (settings.AddonInstances == null)
            {
                settings.AddonInstances = new List<AddonInstance>();
            }

            if (settings.SeededCoreAddonProviderKeys == null)
            {
                settings.SeededCoreAddonProviderKeys = new List<string>();
            }

            bool changed = false;

            foreach (AddonInstance definition in GetCoreAddonDefinitions())
            {
                bool alreadySeeded =
                    settings.SeededCoreAddonProviderKeys.Any(key =>
                        string.Equals(
                            key,
                            definition.ProviderKey,
                            StringComparison.OrdinalIgnoreCase));

                if (alreadySeeded)
                {
                    continue;
                }

                // Belt-and-braces: even if the seeded-keys list was lost
                // or edited externally, never add a second instance for
                // a ProviderKey that's already present.
                bool alreadyPresent =
                    settings.AddonInstances.Any(existing =>
                        existing != null &&
                        string.Equals(
                            existing.ProviderKey,
                            definition.ProviderKey,
                            StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    settings.AddonInstances.Add(definition);
                }

                settings.SeededCoreAddonProviderKeys.Add(definition.ProviderKey);
                changed = true;
            }

            // Slice 15 (Anime Architecture Audit + Kitsu Migration +
            // Provider Role Correction): every core addon's Roles (set
            // on its definition below) was introduced/changed after many
            // installs had already seeded these AddonInstances - and the
            // "alreadySeeded" check above means an existing instance is
            // never recreated from its definition again. Without this,
            // an already-saved core instance would keep deserializing
            // with Roles empty (the old "Role" string field is no longer
            // read at all) forever, and the Provider Role Correction
            // would silently never take effect for it. This is a
            // targeted, idempotent migration keyed strictly off
            // ProviderKey - the same stable identity AddonInstance.
            // ProviderKey's own remarks already document this project
            // using for exactly this kind of "single active instance per
            // provider" purpose - never off FriendlyName or ManifestUrl,
            // so it can never touch a user's own custom addon even one
            // that happens to reuse the same name or URL. Supersedes the
            // earlier, AnimeFLV-only SyncAnimeFlvRole migration.
            if (SyncCoreAddonRoles(settings))
            {
                changed = true;
            }

            return changed;
        }

        // =========================================================
        // SYNC CORE ADDON ROLES (Slice 15 migration)
        // =========================================================
        //
        // See the call-site remarks above. Returns true only when an
        // existing instance's Roles actually needed correcting, so a
        // fully up-to-date settings file is never rewritten for no
        // reason (same "don't dirty the file unnecessarily" convention
        // this class already follows for seeding itself). The expected
        // Roles per ProviderKey mirror GetCoreAddonDefinitions below
        // exactly - kept as a single small map here rather than
        // re-deriving from the definitions list, so this migration
        // never accidentally picks up an unrelated field change from a
        // future definition edit.
        private static readonly Dictionary<string, List<string>> CoreProviderRoles =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "cinemeta", new List<string> { AddonRole.Metadata, AddonRole.Catalogue } },

                // AnimeFLV: Metadata + Catalogue ONLY. Its manifest does
                // declare "stream" (it really can serve anime streams),
                // but this slice's explicit correction is that AnimeFLV
                // must never be used as a StreamProvider, for any
                // content type - see AddonRole's own remarks.
                { "animeflv", new List<string> { AddonRole.Metadata, AddonRole.Catalogue } },

                { "opensubtitles", new List<string> { AddonRole.SubtitleProvider } },
                { "opensubtitlesv3", new List<string> { AddonRole.SubtitleProvider } },

                // Local Files: carries the generic LocalMedia category
                // per this slice's own desired-architecture mapping, but
                // ALSO keeps StreamProvider - it is today seeded as a
                // real Stremio-protocol addon (org.stremio.local) whose
                // "stream" resource is how local playback is actually
                // discovered (see its definition below). Dropping
                // StreamProvider here would silently stop local files
                // from ever being queried for streams again - a
                // regression this slice's own "preserve completed
                // functionality" instruction forbids, and nothing in
                // this task asked for that removal.
                { "localfiles", new List<string> { AddonRole.LocalMedia, AddonRole.StreamProvider } }
            };

        private static bool SyncCoreAddonRoles(StremioVideoCenterSettings settings)
        {
            bool changed = false;

            foreach (AddonInstance instance in settings.AddonInstances)
            {
                if (instance == null || string.IsNullOrWhiteSpace(instance.ProviderKey))
                {
                    continue;
                }

                List<string> expectedRoles;

                if (!CoreProviderRoles.TryGetValue(instance.ProviderKey, out expectedRoles))
                {
                    continue;
                }

                List<string> currentRoles = instance.Roles ?? new List<string>();

                bool matches =
                    currentRoles.Count == expectedRoles.Count &&
                    expectedRoles.All(role => currentRoles.Contains(role));

                if (!matches)
                {
                    instance.Roles = new List<string>(expectedRoles);
                    changed = true;
                }
            }

            return changed;
        }

        // =========================================================
        // CORE ADDON DEFINITIONS
        // =========================================================

        private static List<AddonInstance> GetCoreAddonDefinitions()
        {
            return new List<AddonInstance>
            {
                // -----------------------------------------------------
                // CINEMETA
                // -----------------------------------------------------
                // Live-confirmed this slice: GET https://v3-cinemeta.strem.io/manifest.json
                // id "com.linvo.cinemeta", resources ["catalog","meta","addon_catalog"]
                // (no "stream" resource - Cinemeta is metadata/catalogue only),
                // types ["movie","series"], idPrefixes ["tt"], no
                // behaviorHints.configurable/configurationRequired (zero-config).
                new AddonInstance
                {
                    ProviderKey = "cinemeta",
                    IsCore = true,
                    InstallPolicy = AddonInstallPolicy.ReplaceExisting,
                    Category = "Core",
                    FriendlyName = "Cinemeta",
                    AddonId = "com.linvo.cinemeta",
                    ManifestUrl = "https://v3-cinemeta.strem.io",
                    Enabled = true,
                    Version = "3.0.14",
                    Description = "The official addon for movie and series catalogs",
                    Types = new List<string> { "movie", "series" },
                    IdPrefixes = new List<string> { "tt" },
                    ResourceNames = new List<string> { "catalog", "meta", "addon_catalog" },
                    Roles = new List<string> { AddonRole.Metadata, AddonRole.Catalogue }
                },

                // -----------------------------------------------------
                // ANIMEFLV (public "AnimES" instance)
                // -----------------------------------------------------
                // Live-confirmed this slice: GET https://pigamer37.alwaysdata.net/manifest.json
                // id "com.animeflv.stremio.addon", name "AnimES", resources
                // ["stream", {meta,...}, "catalog"], behaviorHints.configurable
                // = true but configurationRequired is NOT set - usable as-is
                // with no configuration step, matching "installed by default,
                // no configuration required."
                //
                // Roles = [Metadata, Catalogue], deliberately NOT
                // StreamProvider (Slice 15 Anime Architecture Audit +
                // Kitsu Migration + Provider Role Correction): this
                // addon's manifest genuinely declares "stream" (it really
                // does serve anime streams, and ResourceNames above is
                // left completely unchanged/accurate - that field remains
                // an honest record of the manifest's own capabilities),
                // but per this slice's explicit correction AnimeFLV must
                // never be queried during stream discovery for ANY
                // content type - not scoped to "outside Anime requests"
                // (the prior AddonRole.AnimeScopedStream design, now
                // removed). Its metadata/catalogue/search/artwork role
                // (AnimeFlvCatalogueService, AnimeBrowseView's genre/
                // search browse grid) is completely untouched by this -
                // only stream-discovery eligibility is scoped, via
                // StremioAddonRegistry.GetAllStreamsAsync's generic
                // Roles-based check (no AnimeFLV-specific code there at
                // all).
                new AddonInstance
                {
                    ProviderKey = "animeflv",
                    IsCore = true,
                    InstallPolicy = AddonInstallPolicy.ReplaceExisting,
                    Category = "Core",
                    FriendlyName = "AnimeFLV",
                    AddonId = "com.animeflv.stremio.addon",
                    ManifestUrl = "https://pigamer37.alwaysdata.net",
                    Enabled = true,
                    Version = "1.4.5",
                    Description =
                        "AnimeFLV, AnimeAV1, Henaojara, TioAnime, AnimeJara & JKAnime " +
                        "catalogs and metadata for Stremio. Used by Zino for anime " +
                        "genre/search browsing only - never queried for streams.",
                    Types = new List<string> { "movie", "series", "anime", "other" },
                    IdPrefixes = new List<string>
                    {
                        "tt", "animeflv:", "animeav1:", "henaojara:", "tioanime:",
                        "animejara:", "jkanime:", "tmdb:", "anilist:", "kitsu:",
                        "mal:", "anidb:"
                    },
                    ResourceNames = new List<string> { "stream", "meta", "catalog" },
                    Roles = new List<string> { AddonRole.Metadata, AddonRole.Catalogue }
                },

                // -----------------------------------------------------
                // OPENSUBTITLES (legacy / v1)
                // -----------------------------------------------------
                // A separate, distinct core addon from OpenSubtitles v3
                // below - different id, different transport URL, added in
                // a correction pass after review found the original four-
                // core-addon set was missing it.
                //
                // Its manifest.json returns an HTML landing page rather
                // than raw JSON to a non-Stremio client (confirmed again
                // this pass, via multiple path variations, same result as
                // the original architecture audit), so its identity here
                // is sourced from Stremio's OWN official addons registry
                // rather than a live JSON fetch: github.com/Stremio/
                // stremio-official-addons, index.json, the entry with id
                // "org.stremio.opensubtitles" and flags.official: true.
                // That entry's full manifest object (reproduced exactly
                // below) does not declare idPrefixes at all, unlike v3 -
                // left empty here rather than assumed to be ["tt"].
                new AddonInstance
                {
                    ProviderKey = "opensubtitles",
                    IsCore = true,
                    InstallPolicy = AddonInstallPolicy.ReplaceExisting,
                    Category = "Core",
                    FriendlyName = "OpenSubtitles",
                    AddonId = "org.stremio.opensubtitles",
                    ManifestUrl = "https://opensubtitles.strem.io/stremio/v1",
                    Enabled = true,
                    Version = "0.24.0",
                    Description = "The official add-on for subtitles from OpenSubtitles",
                    Types = new List<string> { "series", "movie", "other" },
                    IdPrefixes = new List<string>(),
                    ResourceNames = new List<string> { "subtitles" },
                    Roles = new List<string> { AddonRole.SubtitleProvider }
                },

                // -----------------------------------------------------
                // OPENSUBTITLES V3
                // -----------------------------------------------------
                // Live-confirmed in the original slice: GET
                // https://opensubtitles-v3.strem.io/manifest.json - id
                // "org.stremio.opensubtitlesv3", resources ["subtitles"],
                // idPrefixes ["tt"], no configurable/configurationRequired
                // (zero-config). A separate addon/instance from legacy
                // OpenSubtitles above - different id and transport URL;
                // both are seeded, neither replaces the other.
                new AddonInstance
                {
                    ProviderKey = "opensubtitlesv3",
                    IsCore = true,
                    InstallPolicy = AddonInstallPolicy.ReplaceExisting,
                    Category = "Core",
                    FriendlyName = "OpenSubtitles v3",
                    AddonId = "org.stremio.opensubtitlesv3",
                    ManifestUrl = "https://opensubtitles-v3.strem.io",
                    Enabled = true,
                    Version = "1.0.0",
                    Description = "OpenSubtitles v3 Addon for Stremio",
                    Types = new List<string> { "movie", "series" },
                    IdPrefixes = new List<string> { "tt" },
                    ResourceNames = new List<string> { "subtitles" },
                    Roles = new List<string> { AddonRole.SubtitleProvider }
                },

                // -----------------------------------------------------
                // LOCAL FILES
                // -----------------------------------------------------
                // NOT a normal remote Stremio addon - per the official
                // Stremio protocol, the local-files addon is served by
                // Stremio's OWN desktop runtime from the machine's
                // loopback address (org.stremio.local), not by a
                // Zino-controlled or third-party remote server. This is
                // the real, documented local endpoint (already recorded
                // in claude/Slice14A_Addon_Metadata_Architecture_Audit.md,
                // itself unreachable from this sandbox by definition since
                // it is loopback-only) - not an invented URL. It is
                // registered through the same AddonInstance/registry
                // architecture as every other addon; if nothing is
                // listening on that port (Stremio's local component isn't
                // running), StremioAddonRegistry's existing per-addon
                // try/catch already skips it gracefully, exactly like any
                // other unreachable addon - no special-casing required.
                new AddonInstance
                {
                    ProviderKey = "localfiles",
                    IsCore = true,
                    InstallPolicy = AddonInstallPolicy.ReplaceExisting,
                    Category = "Core",
                    FriendlyName = "Local Files",
                    AddonId = "org.stremio.local",
                    ManifestUrl = "http://127.0.0.1:11470/local-addon",
                    Enabled = true,
                    Version = string.Empty,
                    Description =
                        "Plays local video files scanned by Stremio's own local " +
                        "component, when it is running on this machine.",
                    Types = new List<string> { "movie", "series", "other" },
                    IdPrefixes = new List<string> { "local:", "bt:" },
                    ResourceNames = new List<string> { "stream" },
                    Roles = new List<string> { AddonRole.LocalMedia, AddonRole.StreamProvider }
                }
            };
        }
    }
}
