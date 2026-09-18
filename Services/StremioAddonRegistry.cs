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
            public StremioAddonService Service;
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

        public void AddAddon(string addonUrl)
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
                Service = new StremioAddonService(trimmed)
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
