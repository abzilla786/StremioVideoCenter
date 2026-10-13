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
        private List<string> roles = new List<string>();
        private List<AddonResourceDeclaration> resources = new List<AddonResourceDeclaration>();
        private bool configurable = false;
        private bool configurationRequired = false;

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
        // PER-RESOURCE DECLARATIONS (Slice 20 Stage 1B)
        // =========================================================
        //
        // ResourceNames above is the flattened list of resource names
        // and is kept exactly as it was (stream discovery reads it).
        // This list additionally keeps, per resource, the manifest's own
        // "types" and "idPrefixes" for that resource when it declared
        // them (the object form of a "resources" entry). Empty for an
        // instance saved before this slice or never refreshed: callers
        // treat that as "no per-resource restriction known" and fall
        // back to the manifest-level Types/IdPrefixes above (see
        // AddonResourceCompatibility).
        public List<AddonResourceDeclaration> Resources
        {
            get => resources;
            set => SetValue(ref resources, value);
        }

        // Manifest behaviorHints.configurable / configurationRequired,
        // captured on install and on manifest refresh. False means "not
        // declared or not known" - never an assertion that the addon is
        // ready. See ConfigurationStatusText.
        public bool Configurable
        {
            get => configurable;
            set => SetValue(ref configurable, value);
        }

        public bool ConfigurationRequired
        {
            get => configurationRequired;
            set => SetValue(ref configurationRequired, value);
        }

        // Display-only summary for the addon settings list. Computed,
        // never persisted. Contains no URL or configuration data.
        [DontSerialize]
        public string ConfigurationStatusText
        {
            get
            {
                if (configurationRequired)
                {
                    return "Configuration required";
                }

                // Built-in registration (Local Files): no address and no
                // resources by design, so "use Refresh" would mislead.
                if (string.IsNullOrWhiteSpace(manifestUrl) &&
                    roles != null &&
                    roles.Contains(AddonRole.LocalMedia))
                {
                    return "Built in - uses your local media library";
                }

                if (resourceNames == null || resourceNames.Count == 0)
                {
                    return "Manifest details not loaded - use Refresh";
                }

                return configurable ? "Ready (configurable)" : "Ready";
            }
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

        // =========================================================
        // ROLES (Zino's own intended-usage assignment, generic)
        // =========================================================
        //
        // Slice 15 (Anime Architecture Audit + Kitsu Migration + Provider
        // Role Correction) - replaces the earlier single Role(string)
        // field (and its AddonRole.AnimeScopedStream value, now removed
        // entirely - see AddonRole's own remarks below for why). An
        // addon can hold more than one role at once (e.g. AnimeFLV is
        // both Metadata and Catalogue, but never StreamProvider) - a
        // single string could not express that, so this is a list.
        //
        // Empty/null (the default, and what every pre-existing
        // AddonInstance deserializes as, since the old "Role" field is
        // no longer read) means "no explicit role assigned" - callers
        // that gate on a specific role (StremioAddonRegistry.
        // GetAllStreamsAsync's StreamProvider check) must treat an
        // empty/null Roles list as "role unknown, fall back to prior
        // behaviour" (the existing ResourceNames-capability check),
        // exactly like AddonEntry.ResourceNames already does for an
        // unknown capability list. This preserves every pre-existing
        // and future Torrentio/Comet/custom addon's "always queried for
        // stream discovery" behaviour unchanged unless Roles is
        // explicitly populated for it.
        public List<string> Roles
        {
            get => roles;
            set => SetValue(ref roles, value);
        }
    }

    // =============================================================
    // ADDON RESOURCE DECLARATION (Slice 20 Stage 1B)
    // =============================================================
    //
    // One entry of a manifest's "resources" array, as stored on an
    // AddonInstance: the resource name plus the types/idPrefixes that
    // entry declared for itself. Types/IdPrefixes are null/empty when
    // the manifest listed the resource as a bare string (or omitted
    // them), meaning "inherit the manifest-level values".
    public class AddonResourceDeclaration
    {
        public string Name { get; set; }

        public List<string> Types { get; set; }

        public List<string> IdPrefixes { get; set; }
    }

    // =============================================================
    // ADDON RESOURCE COMPATIBILITY (Slice 20 Stage 1B)
    // =============================================================
    //
    // Answers "may this addon be asked for this resource for this type
    // and content id?" using only what the manifest declared. Per the
    // Stremio protocol a resource's own types/idPrefixes override the
    // manifest-level ones. A restriction that is missing or empty is
    // UNKNOWN, not "nothing allowed": it never excludes the addon.
    // Used by the generic resource query (AddonResourceQuery) and, for
    // the id prefix only, by stream discovery
    // (StremioAddonRegistry.GetAllStreamsAsync).
    public static class AddonResourceCompatibility
    {
        // True when the instance's known resource list names the
        // resource. An unknown (empty) resource list is "not declared"
        // here - callers that want the unknown-capability fallback must
        // check AddonCapabilities.HasKnownCapabilities themselves.
        public static bool Declares(AddonInstance instance, string resource)
        {
            if (instance == null ||
                instance.ResourceNames == null ||
                string.IsNullOrWhiteSpace(resource))
            {
                return false;
            }

            return instance.ResourceNames.Exists(name =>
                string.Equals(name, resource, System.StringComparison.OrdinalIgnoreCase));
        }

        public static bool AllowsContent(
            AddonInstance instance,
            string resource,
            string type,
            string id)
        {
            if (instance == null)
            {
                return false;
            }

            AddonResourceDeclaration declaration = null;

            if (instance.Resources != null)
            {
                declaration = instance.Resources.Find(entry =>
                    entry != null &&
                    string.Equals(entry.Name, resource, System.StringComparison.OrdinalIgnoreCase));
            }

            List<string> types =
                declaration != null && declaration.Types != null && declaration.Types.Count > 0
                    ? declaration.Types
                    : instance.Types;

            List<string> prefixes =
                declaration != null && declaration.IdPrefixes != null && declaration.IdPrefixes.Count > 0
                    ? declaration.IdPrefixes
                    : instance.IdPrefixes;

            if (types != null &&
                types.Count > 0 &&
                !string.IsNullOrWhiteSpace(type) &&
                !types.Exists(candidate =>
                    string.Equals(candidate, type, System.StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (prefixes != null &&
                prefixes.Count > 0 &&
                !string.IsNullOrWhiteSpace(id) &&
                !prefixes.Exists(prefix =>
                    !string.IsNullOrEmpty(prefix) &&
                    id.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
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
    // ADDON ROLE (generic capability/usage model)
    // =============================================================
    //
    // Slice 15 (Anime Architecture Audit + Kitsu Migration + Provider
    // Role Correction) - supersedes the earlier Workstream B design,
    // which introduced a single, provider-shaped AddonRole.
    // AnimeScopedStream value (content-type-conditional stream
    // eligibility for AnimeFLV specifically). That design is REMOVED
    // entirely per this slice's explicit correction: AnimeFLV must
    // never be queried for playable stream URLs, for ANY content type
    // (Movie, TV, Anime alike) - not "only outside Anime requests".
    // There is deliberately no provider-named role of any kind here -
    // only generic capability/usage categories that any addon, present
    // or future, can carry:
    //
    //   Metadata        - the addon can supply title/overview/artwork/
    //                      episode metadata (Stremio's "meta" resource).
    //   Catalogue       - the addon can supply browsable/searchable
    //                      catalogue rows (Stremio's "catalog" resource).
    //   StreamProvider  - the addon's stream results should be included
    //                      in ordinary stream discovery
    //                      (StremioAddonRegistry.GetAllStreamsAsync).
    //   SubtitleProvider - the addon supplies subtitles (Stremio's
    //                      "subtitles" resource). Not yet consulted by
    //                      any discovery code this slice (subtitle
    //                      discovery/UI is a later slice) - recorded
    //                      now so the capability model is complete and
    //                      a future subtitle-provider addon (anime or
    //                      otherwise) never needs another role redesign.
    //   LocalMedia      - the addon (or a future dedicated local-
    //                      library feature) sources content from the
    //                      local filesystem rather than a remote addon
    //                      backend.
    //
    // An addon can carry more than one of these at once (e.g. AnimeFLV
    // = Metadata + Catalogue, never StreamProvider) - see
    // AddonInstance.Roles. Narrows WHEN an addon's already-declared
    // "stream" resource capability (AddonCapabilities.Streams below)
    // should actually be exercised during stream discovery - it does
    // not change what an addon IS capable of per its manifest
    // (ResourceNames remains the single source of truth for that),
    // only when Zino chooses to ask it for streams. Checked in addition
    // to - never instead of - the pre-existing ResourceNames-based skip
    // in GetAllStreamsAsync: that check answers "can this addon serve
    // streams at all" (e.g. Cinemeta cannot); this model answers "does
    // Zino intend to use this addon for streams at all" (e.g. AnimeFLV
    // can, per its manifest, but Zino deliberately never asks it to).
    //
    // Free-form string constants, matching this project's existing
    // Category/InstallPolicy convention (see their own remarks above),
    // so a future role never requires a settings migration. No addon
    // name appears anywhere in this class or in the code that consults
    // it (StremioAddonRegistry.GetAllStreamsAsync) - see
    // InferFromResourceNames below for how a role is derived generically
    // from a manifest's own declared resources, with no per-provider
    // special-casing.
    public static class AddonRole
    {
        public const string Metadata = "Metadata";
        public const string Catalogue = "Catalogue";
        public const string StreamProvider = "StreamProvider";
        public const string SubtitleProvider = "SubtitleProvider";
        public const string LocalMedia = "LocalMedia";

        // =====================================================
        // INFER FROM RESOURCE NAMES (generic manifest -> Roles)
        // =====================================================
        //
        // Used by the generic addon-install flow (Torrentio/Comet/
        // Custom - see StremioVideoCenterSettingsView.
        // InstallAddonFromManifestUrlAsync) to populate a newly
        // installed instance's Roles directly from its own fetched
        // manifest's resource names - the same AddonCapabilities.*
        // constants (below) already derived from that manifest, so
        // this is a pure relabeling, not a second source of truth.
        // Applied uniformly regardless of provider name - there is no
        // per-addon branch here. This is deliberately NOT used for the
        // five core addons (CoreAddonSeedingService assigns their
        // Roles explicitly) because at least one of them - AnimeFLV -
        // needs its assigned Roles to intentionally differ from what
        // this generic inference would produce from its manifest (its
        // manifest genuinely declares "stream", but Zino must never
        // treat it as a StreamProvider) - exactly the distinction the
        // class remarks above describe.
        public static List<string> InferFromResourceNames(List<string> resourceNames)
        {
            var inferred = new List<string>();

            if (resourceNames == null || resourceNames.Count == 0)
            {
                return inferred;
            }

            if (resourceNames.Contains(AddonCapabilities.Streams))
            {
                inferred.Add(StreamProvider);
            }

            if (resourceNames.Contains(AddonCapabilities.Metadata))
            {
                inferred.Add(Metadata);
            }

            if (resourceNames.Contains(AddonCapabilities.Catalogue))
            {
                inferred.Add(Catalogue);
            }

            if (resourceNames.Contains(AddonCapabilities.Subtitles))
            {
                inferred.Add(SubtitleProvider);
            }

            return inferred;
        }
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
