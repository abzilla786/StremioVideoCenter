using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Holds a set of configured Stremio addon base URLs and can fan a stream
    // request out across all of them, aggregating results. Each addon is
    // represented independently as its own StremioAddonService instance -
    // this does not change or wrap StremioAddonService's own behaviour at
    // all, it just manages a collection of them.
    //
    // Now wired into the live discovery/playback path (via
    // StremioContentDiscoveryService and StremioVideoCenterView's
    // PlaySelectedMediaAsync) as of the configurable-addon slice.
    //
    // Tracks each addon's URL alongside its StremioAddonService instance
    // internally, rather than reading it back off the service, because
    // StremioAddonService exposes no public URL getter and is protected
    // from modification.
    public class StremioAddonRegistry
    {
        private sealed class AddonEntry
        {
            public string Url;
            public string FriendlyName;
            public StremioAddonService Service;

            // Capability-aware addon architecture (Cinemeta-First Addon
            // Settings Architecture slice). Null/empty means "capability
            // unknown" (e.g. a legacy entry from FromConfiguredUrls,
            // which never captured a manifest) - GetAllStreamsAsync
            // treats unknown capabilities as "query it anyway", exactly
            // preserving prior behaviour for every pre-existing entry.
            // Only an entry with a KNOWN resource list that does NOT
            // include "stream" (e.g. Cinemeta: catalog/meta/addon_catalog
            // only) is skipped.
            public List<string> ResourceNames;
        }

        private readonly List<AddonEntry> _entries = new List<AddonEntry>();

        public StremioAddonRegistry()
        {
        }

        public StremioAddonRegistry(IEnumerable<string> addonUrls)
        {
            if (addonUrls != null)
            {
                foreach (string url in addonUrls)
                {
                    AddAddon(url);
                }
            }
        }

        // Builds a registry from the raw newline-delimited
        // StremioAddonUrls setting value. Trims whitespace, ignores blank
        // lines, and ignores duplicate entries (case-insensitive) - all via
        // AddAddon below, so this factory and the constructor share the
        // exact same filtering behaviour regardless of input source.
        public static StremioAddonRegistry FromConfiguredUrls(string newlineDelimitedUrls)
        {
            var registry = new StremioAddonRegistry();

            if (string.IsNullOrWhiteSpace(newlineDelimitedUrls))
            {
                return registry;
            }

            string[] lines = newlineDelimitedUrls.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                registry.AddAddon(line);
            }

            return registry;
        }

        // Builds a registry from structured addon instances (the
        // AddonInstances setting), added alongside FromConfiguredUrls
        // above rather than replacing it - both remain valid ways to
        // build a registry. Only enabled instances with a non-empty
        // ManifestUrl are registered; disabled instances are skipped
        // entirely rather than being added and immediately excluded, so
        // a disabled/malformed instance can never affect discovery.
        // Duplicate manifest URLs across instances are still
        // deduplicated by AddAddon exactly as for the legacy path.
        public static StremioAddonRegistry FromAddonInstances(
            IEnumerable<AddonInstance> addonInstances)
        {
            var registry = new StremioAddonRegistry();

            if (addonInstances == null)
            {
                return registry;
            }

            foreach (AddonInstance instance in addonInstances)
            {
                if (instance == null || !instance.Enabled)
                {
                    continue;
                }

                // Display-safe identity for this addon: prefer the
                // user-facing FriendlyName (e.g. "Torrentio RD"), falling
                // back to the addon's own manifest id if the instance was
                // never given a friendly name. Never the manifest URL -
                // for a Real-Debrid-configured addon that URL carries the
                // user's RD key, and it must never reach a UI or log
                // through this path (see SourceSelectorView/AddonEntry).
                string displayName =
                    !string.IsNullOrWhiteSpace(instance.FriendlyName)
                        ? instance.FriendlyName
                        : instance.AddonId;

                registry.AddAddon(instance.ManifestUrl, displayName, instance.ResourceNames);
            }

            return registry;
        }

        public IReadOnlyList<string> AddonUrls
        {
            get
            {
                List<string> urls = new List<string>();

                foreach (AddonEntry entry in _entries)
                {
                    urls.Add(entry.Url);
                }

                return urls;
            }
        }

        // friendlyName is optional (defaults to null) so existing callers
        // that only ever had a bare URL - e.g. FromConfiguredUrls, the
        // legacy StremioAddonUrls path - keep working unchanged. A null/
        // empty friendly name just means GetAllStreamsAsync tags streams
        // from this addon with no display name; callers reading it back
        // (Source Selector, diagnostics) fall back to a generic label,
        // never to the URL itself.
        //
        // resourceNames is optional too (defaults to null, meaning
        // "capability unknown"). Only FromAddonInstances currently
        // passes it - see AddonEntry.ResourceNames remarks above for how
        // GetAllStreamsAsync treats it.
        public void AddAddon(string addonUrl, string friendlyName = null, List<string> resourceNames = null)
        {
            if (string.IsNullOrWhiteSpace(addonUrl))
            {
                return;
            }

            string trimmed = addonUrl.Trim();

            foreach (AddonEntry entry in _entries)
            {
                if (string.Equals(entry.Url, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    // Duplicate - already registered, skip.
                    return;
                }
            }

            _entries.Add(new AddonEntry
            {
                Url = trimmed,
                FriendlyName = friendlyName,
                Service = new StremioAddonService(trimmed),
                ResourceNames = resourceNames
            });
        }

        // Requests streams from every registered addon and aggregates the
        // results, tagging each stream with the addon URL it came from. An
        // addon that fails (unreachable, bad manifest, malformed response,
        // etc.) is skipped rather than failing the whole request - one
        // broken addon should never block streams from the others.
        public async Task<List<StremioStream>> GetAllStreamsAsync(string type, string id)
        {
            List<StremioStream> combined = new List<StremioStream>();

            foreach (AddonEntry entry in _entries)
            {
                // Capability-aware skip: only skip when this addon's
                // resource list is KNOWN (non-null/non-empty) and does
                // NOT include "stream" - e.g. Cinemeta (catalog/meta/
                // addon_catalog only), which would otherwise 404 on
                // every single stream request. An entry with unknown
                // capabilities (null/empty ResourceNames - every legacy
                // FromConfiguredUrls entry, and any AddonInstance saved
                // before this slice) is always queried, exactly
                // preserving prior behaviour.
                if (entry.ResourceNames != null &&
                    entry.ResourceNames.Count > 0 &&
                    !entry.ResourceNames.Contains("stream"))
                {
                    continue;
                }

                try
                {
                    List<StremioStream> streams = await entry.Service.GetStreamsAsync(type, id);

                    if (streams != null)
                    {
                        foreach (StremioStream stream in streams)
                        {
                            if (stream != null)
                            {
                                stream.SourceAddonUrl = entry.Url;
                                stream.SourceAddonName = entry.FriendlyName;
                            }

                            combined.Add(stream);
                        }
                    }
                }
                catch
                {
                    // Skip this addon; others may still succeed.
                }
            }

            return combined;
        }

        // Tests every registered addon's manifest independently. One addon
        // failing (unreachable, malformed manifest, etc.) never prevents the
        // others from being tested - each gets its own try/catch and its own
        // reported result.
        public async Task<List<AddonTestResult>> TestAllAddonsAsync()
        {
            List<AddonTestResult> results = new List<AddonTestResult>();

            foreach (AddonEntry entry in _entries)
            {
                AddonTestResult result = new AddonTestResult
                {
                    AddonUrl = entry.Url
                };

                try
                {
                    StremioAddonManifest manifest = await entry.Service.GetManifestAsync();

                    if (manifest == null)
                    {
                        result.Success = false;
                        result.Message = "No manifest returned.";
                    }
                    else
                    {
                        result.Success = true;

                        result.Message =
                            (string.IsNullOrWhiteSpace(manifest.Name) ? "(unnamed addon)" : manifest.Name) +
                            (string.IsNullOrWhiteSpace(manifest.Version) ? string.Empty : " v" + manifest.Version);
                    }
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = ex.Message;
                }

                results.Add(result);
            }

            return results;
        }
    }

    // =============================================================
    // ADDON TEST RESULT
    // =============================================================

    public class AddonTestResult
    {
        public string AddonUrl { get; set; }

        public bool Success { get; set; }

        public string Message { get; set; }
    }
}
