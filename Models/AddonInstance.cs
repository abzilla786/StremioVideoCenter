using Playnite.SDK.Data;
using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // =========================================================
    // ADDON INSTANCE
    // =========================================================
    //
    // Represents one configured Stremio addon - a specific manifest
    // URL, independent of which provider it points to. A user can have
    // multiple instances of the same provider (e.g. two differently
    // configured Torrentio manifests) because a Stremio addon
    // "configuration" is baked into the manifest URL itself per the
    // addon protocol - there is no separate config payload to store.
    //
    // This is the structured, additive counterpart to the legacy flat
    // StremioVideoCenterSettings.StremioAddonUrls newline-delimited
    // setting. See StremioVideoCenterSettings.SyncLegacyAddonUrls for
    // the backward-compatible migration between the two - the legacy
    // setting is never modified or cleared by that migration.
    //
    // Uses the same ObservableObject/SetValue pattern as
    // StremioVideoCenterSettings so Enabled can be two-way bound
    // directly from a Settings UI CheckBox.
    public class AddonInstance : ObservableObject
    {
        private string friendlyName = string.Empty;
        private string addonId = string.Empty;
        private string manifestUrl = string.Empty;
        private bool enabled = true;
        private string category = "Custom";
        private string version = string.Empty;
        private string description = string.Empty;
        private string logo = string.Empty;
        private List<string> resourceNames = new List<string>();
        private List<string> types = new List<string>();
        private List<string> idPrefixes = new List<string>();
        private string providerKey = string.Empty;
        private bool isCore = false;
        private string installPolicy = AddonInstallPolicy.AllowMultiple;

        // Display name shown in the Settings UI. Falls back to the
        // manifest's own "name" field (copied in on import) when the
        // user has not renamed the instance.
        public string FriendlyName
        {
            get => friendlyName;
            set => SetValue(ref friendlyName, value);
        }

        // The manifest's own "id" field (e.g. "com.stremio.torrentio"),
        // captured on import. Not used for de-duplication today - two
        // differently-configured instances of the same provider share
        // the same AddonId by design - but kept for display/diagnostics
        // and to allow future provider-specific handling if ever needed.
        public string AddonId
        {
            get => addonId;
            set => SetValue(ref addonId, value);
        }

        // The full, already-configured manifest URL (e.g.
        // ".../manifest.json" including any baked-in configuration
        // segment). This is what StremioAddonService is constructed
        // from - identical in shape to a legacy StremioAddonUrls line.
        public string ManifestUrl
        {
            get => manifestUrl;
            set => SetValue(ref manifestUrl, value);
        }

        // Disabled instances are kept in settings but excluded when
        // building the live addon registry (StremioAddonRegistry.
        // FromAddonInstances), so the user can turn an addon off
        // without losing its configured URL.
        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        // "Recommended" (Torrentio/Comet installed via the
        // Configure & Install flow), "Custom" (pasted manifest URL),
        // or "Legacy" (migrated from StremioAddonUrls). Drives which
        // section of the Settings UI an instance is grouped under.
        // Free-form string rather than an enum so future categories
        // don't require a settings migration.
        public string Category
        {
            get => category;
            set => SetValue(ref category, value);
        }

        // Manifest capability snapshot, captured on successful import
        // so the Settings UI can display them without re-fetching the
        // manifest every time the panel opens.
        public string Version
        {
            get => version;
            set => SetValue(ref version, value);
        }

        public string Description
        {
            get => description;
            set => SetValue(ref description, value);
        }

        public string Logo
        {
            get => logo;
            set => SetValue(ref logo, value);
        }

        public List<string> ResourceNames
        {
            get => resourceNames;
            set => SetValue(ref resourceNames, value);
        }

        public List<string> Types
        {
            get => types;
            set => SetValue(ref types, value);
        }

        public List<string> IdPrefixes
        {
            get => idPrefixes;
            set => SetValue(ref idPrefixes, value);
        }

        // =========================================================
        // PROVIDER KEY / INSTALL POLICY / CORE FLAG
        // =========================================================
        //
        // Cinemeta-First Addon Settings Architecture slice additions.
        // All three are additive - existing instances created before this
        // slice simply have ProviderKey = "" and InstallPolicy =
        // "AllowMultiple" (the default), which preserves their exact
        // prior behaviour (never auto-replaced, never treated as core).

        // Stable identity for a "single active instance" provider, e.g.
        // "torrentio" or "comet" - never the manifest's own AddonId
        // (which is only known AFTER a fetch, and isn't guaranteed
        // stable across every third-party fork/rebrand). Empty/null for
        // a generic custom addon, which never participates in replace
        // semantics regardless of InstallPolicy.
        public string ProviderKey
        {
            get => providerKey;
            set => SetValue(ref providerKey, value);
        }

        // True only for the fixed set of zero-config addons seeded by
        // CoreAddonSeedingService on first run (Cinemeta, AnimeFLV,
        // OpenSubtitles v3, Local Files). Drives the "CORE / DEFAULT"
        // grouping in Settings - never set by the user-facing installers.
        public bool IsCore
        {
            get => isCore;
            set => SetValue(ref isCore, value);
        }

        // One of AddonInstallPolicy.ReplaceExisting /
        // AddonInstallPolicy.AllowMultiple. Free-form string, matching
        // this class's existing Category convention, so a future policy
        // value never requires a settings migration.
        public string InstallPolicy
        {
            get => installPolicy;
            set => SetValue(ref installPolicy, value);
        }
    }

    // =============================================================
    // ADDON INSTALL POLICY
    // =============================================================
    //
    // ReplaceExisting: installing a new instance for the same ProviderKey
    // atomically replaces the existing one (Torrentio, Comet) - see
    // StremioVideoCenterSettings.InstallOrReplaceAddonInstance. Never
    // more than one active instance per ProviderKey.
    //
    // AllowMultiple: the existing add-only behaviour (generic custom
    // addons, e.g. two differently-configured MediaFusion instances).
    public static class AddonInstallPolicy
    {
        public const string ReplaceExisting = "ReplaceExisting";
        public const string AllowMultiple = "AllowMultiple";
    }

    // =============================================================
    // ADDON CAPABILITIES
    // =============================================================
    //
    // Small, read-only helper over AddonInstance.ResourceNames (itself
    // already populated verbatim from the manifest's "resources" array -
    // see StremioAddonService.ParseResources). Not a new persisted field:
    // the manifest's own resource names ARE the capability model: "meta"
    // (Metadata), "catalog" (Catalogue), "stream" (Streams), "subtitles"
    // (Subtitles), "addon_catalog" (Addon Catalogue). This mapping exists
    // purely to answer "does this addon support X" and to label
    // capabilities for display, without requiring every addon to support
    // every resource - e.g. Cinemeta has Metadata/Catalogue but no
    // Streams; Torrentio/Comet have Streams but no Metadata/Catalogue.
    public static class AddonCapabilities
    {
        public const string Metadata = "meta";
        public const string Catalogue = "catalog";
        public const string Streams = "stream";
        public const string Subtitles = "subtitles";
        public const string AddonCatalogue = "addon_catalog";

        // An instance with no known resource data (e.g. a legacy entry
        // migrated from the plain StremioAddonUrls textbox, which never
        // captured a manifest) is treated as "capability unknown" rather
        // than "capability absent" - callers should default to their
        // prior behaviour (e.g. still querying it for streams) rather
        // than silently excluding it. Supports() itself only answers
        // "does the known resource list contain this capability" -
        // callers decide how to treat an empty/unknown list.
        public static bool Supports(AddonInstance instance, string capability)
        {
            if (instance == null ||
                instance.ResourceNames == null ||
                instance.ResourceNames.Count == 0)
            {
                return false;
            }

            return instance.ResourceNames.Contains(capability);
        }

        public static bool HasKnownCapabilities(AddonInstance instance)
        {
            return instance != null &&
                   instance.ResourceNames != null &&
                   instance.ResourceNames.Count > 0;
        }

        public static string ToFriendlyLabel(string capability)
        {
            if (capability == Metadata)
            {
                return "Metadata";
            }

            if (capability == Catalogue)
            {
                return "Catalogue";
            }

            if (capability == Streams)
            {
                return "Streams";
            }

            if (capability == Subtitles)
            {
                return "Subtitles";
            }

            if (capability == AddonCatalogue)
            {
                return "Addon Catalogue";
            }

            return capability;
        }
    }
}
