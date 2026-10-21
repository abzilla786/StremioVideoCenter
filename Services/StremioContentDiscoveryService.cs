using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // The single entry point for "find me the Stremio streams for this
    // piece of content", covering movies, TV episodes, and anime episodes
    // uniformly through ContentRequest. This is a discovery-only service:
    // it produces a raw List<StremioStream> and nothing more. It does NOT
    // touch StreamResolverService or RealDebridService, and it never
    // constructs a PlaybackSource or calls the player - resolving streams
    // into something playable is a separate concern, deliberately kept out
    // of this class.
    public class StremioContentDiscoveryService
    {
        private readonly StremioAddonRegistry _registry;

        // Takes a StremioAddonRegistry (multiple configured addons,
        // aggregated) rather than a single StremioAddonService. This is the
        // one call-site-breaking change in the configurable-addon slice;
        // ResolveStreamsAsync's own signature and return type are otherwise
        // completely unchanged.
        public StremioContentDiscoveryService(StremioAddonRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            _registry = registry;
        }

        public async Task<List<StremioStream>> ResolveStreamsAsync(
            ContentRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            string stremioId = request.BuildStremioId();


            if (string.IsNullOrWhiteSpace(stremioId))
            {
                // Not a bug - this is the honest "we don't have enough
                // information to ask Stremio for anything" case: no IMDb
                // mapping for this title (common for some anime), or a
                // TV/anime request missing season or episode. Report it as
                // an empty result rather than guessing or throwing.

                return new List<StremioStream>();
            }


            // Slice 15 (Anime Architecture Audit + Kitsu Migration +
            // Provider Role Correction): GetAllStreamsAsync no longer
            // takes a content-type flag - stream-provider eligibility is
            // decided entirely by each addon's own Roles (see
            // StremioAddonRegistry.GetAllStreamsAsync and AddonRole's
            // own remarks in AddonInstance.cs), never by whether this
            // particular request happens to be Anime.
            List<StremioStream> streams = await _registry.GetAllStreamsAsync(
                request.StremioType,
                stremioId,
                cancellationToken);


            return streams ?? new List<StremioStream>();
        }
    }
}
