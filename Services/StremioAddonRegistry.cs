using StremioVideoCenter.Models;
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
    // Infrastructure only for this phase: not yet wired into the live
    // playback call site (which still talks to the single existing test
    // addon directly, per the agreed minimal change to GetStreamsAsync()).
    // This exists so multi-addon support has somewhere to live once addon
    // configuration/settings UI is built later, without needing a redesign
    // at that point.
    public class StremioAddonRegistry
    {
        private readonly List<StremioAddonService> _addons = new List<StremioAddonService>();

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

        public IReadOnlyList<StremioAddonService> Addons
        {
            get { return _addons; }
        }

        public void AddAddon(string addonUrl)
        {
            if (string.IsNullOrWhiteSpace(addonUrl))
            {
                return;
            }

            _addons.Add(new StremioAddonService(addonUrl));
        }

        // Requests streams from every registered addon and aggregates the
        // results. An addon that fails (unreachable, bad manifest, malformed
        // response, etc.) is skipped rather than failing the whole request -
        // one broken addon should never block streams from the others.
        public async Task<List<StremioStream>> GetAllStreamsAsync(string type, string id)
        {
            List<StremioStream> combined = new List<StremioStream>();

            foreach (StremioAddonService addon in _addons)
            {
                try
                {
                    List<StremioStream> streams = await addon.GetStreamsAsync(type, id);

                    if (streams != null)
                    {
                        combined.AddRange(streams);
                    }
                }
                catch
                {
                    // Skip this addon; others may still succeed.
                }
            }

            return combined;
        }
    }
}
