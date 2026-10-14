using StremioVideoCenter.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // The bridge between a single Stremio stream and something the player
    // can actually play. This is the ONLY thing that knows both "Stremio
    // stream shapes" and "what a playable source looks like" - MpvPlayer/
    // PlayerSession never see a StremioStream, and StremioAddonService never
    // needs to know Real-Debrid exists.
    //
    // Deliberately resolves ONE stream at a time rather than picking among a
    // list itself - "try streams in order until one resolves" is playback
    // policy, and that decision belongs to the caller (StremioVideoCenterView),
    // not baked into this service.
    public class StreamResolverService
    {
        private readonly RealDebridService _realDebrid;

        // realDebrid may be null - that's valid and expected when the user
        // hasn't configured a Real-Debrid token. Direct-URL streams still
        // resolve fine with no Real-Debrid at all; only InfoHash-based
        // streams require it, and that requirement surfaces as a clear
        // exception rather than a silent failure.
        public StreamResolverService(RealDebridService realDebrid)
        {
            _realDebrid = realDebrid;
        }

        public async Task<PlaybackSource> ResolveStreamAsync(StremioStream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // Direct HTTP(S) URL - already playable. This is the path our
            // existing test addon uses, and it must keep working with zero
            // Real-Debrid involvement. Checked first and unconditionally:
            // a stream with both Url and InfoHash populated is still Direct,
            // since this branch runs regardless of InfoHash's presence.
            if (!string.IsNullOrWhiteSpace(stream.Url))
            {
                return new PlaybackSource
                {
                    Url = stream.Url,
                    Title = ChooseTitle(stream),
                    Kind = PlaybackSourceKind.Direct
                };
            }

            // Torrent-based stream - needs Real-Debrid to become a direct URL.
            if (!string.IsNullOrWhiteSpace(stream.InfoHash))
            {
                if (_realDebrid == null)
                {
                    // RealDebridException derives from InvalidOperationException,
                    // so this is still the same exception family as before;
                    // the typed reason lets the caller word it precisely.
                    throw new RealDebridException(
                        RealDebridFailureReason.NotConfigured,
                        "This stream requires Real-Debrid, but no Real-Debrid credential has been configured.");
                }



                string directUrl = await _realDebrid.ResolveMagnetAsync(stream.InfoHash, stream.FileIdx);


                return new PlaybackSource
                {
                    Url = directUrl,
                    Title = ChooseTitle(stream),
                    Kind = PlaybackSourceKind.RealDebrid
                };
            }

            // ExternalUrl-only streams (meant to be opened in an outside
            // app/browser, e.g. a paid-service watch page) are explicitly
            // unsupported for in-app playback this phase - not a bug, a
            // deliberate scope boundary.
            throw new NotSupportedException(
                "This stream has no direct URL or torrent source that can be resolved for in-app playback.");
        }

        private static string ChooseTitle(StremioStream stream)
        {
            if (!string.IsNullOrWhiteSpace(stream.Title))
            {
                return stream.Title;
            }

            if (!string.IsNullOrWhiteSpace(stream.Name))
            {
                return stream.Name;
            }

            return null;
        }
    }
}
