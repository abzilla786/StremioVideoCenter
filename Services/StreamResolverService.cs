using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
            //
            // Slice 22: "direct" now means a well-formed absolute http(s)
            // URL. Anything else in "url" (file://, smb://, edl://, a
            // relative path, text) is NOT handed to mpv, whose own
            // protocol handlers can read local files. A stream whose url is
            // unusable but which also carries an infoHash falls through to
            // the torrent path below.
            if (IsPlayableDirectUrl(stream.Url))
            {
                return new PlaybackSource
                {
                    Url = stream.Url.Trim(),
                    Title = ChooseTitle(stream),
                    Kind = PlaybackSourceKind.Direct,
                    Headers = BuildRequestHeaders(stream)
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
            throw new NotSupportedException(DescribeUnsupported(stream));
        }

        // True only for an absolute http or https URL with a host. Used by
        // both this resolver and the aggregation service so that "has a
        // direct URL" means the same thing in both places.
        public static bool IsPlayableDirectUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            Uri uri;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }

            return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                   !string.IsNullOrEmpty(uri.Host);
        }

        // Fixed, log-safe text (never built from stream content) saying why
        // a stream with no usable source cannot be played in-app.
        public static string DescribeUnsupported(StremioStream stream)
        {
            if (stream != null && !string.IsNullOrWhiteSpace(stream.Url))
            {
                return "This stream's URL is not an http(s) address that can be played.";
            }

            if (stream != null && !string.IsNullOrWhiteSpace(stream.NzbUrl))
            {
                return "Usenet (NZB) streams are not supported.";
            }

            if (stream != null && !string.IsNullOrWhiteSpace(stream.YtId))
            {
                return "YouTube streams are not supported for in-app playback.";
            }

            if (stream != null && !string.IsNullOrWhiteSpace(stream.ExternalUrl))
            {
                return "This stream opens in an external app or website and cannot be played in-app.";
            }

            return "This stream has no direct URL or torrent source that can be resolved for in-app playback.";
        }

        // Header names are HTTP tokens; values must not contain control
        // characters (header injection). Hop-by-hop / framing headers are
        // never forwarded. Counts and lengths are bounded. Returns null
        // when there is nothing usable, so the common case adds no work.
        private static readonly Regex HeaderNamePattern =
            new Regex(@"^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$", RegexOptions.Compiled);

        private const int MaxRequestHeaders = 16;
        private const int MaxRequestHeaderValueLength = 4096;

        internal static Dictionary<string, string> BuildRequestHeaders(StremioStream stream)
        {
            if (stream == null ||
                stream.BehaviorHints == null ||
                stream.BehaviorHints.ProxyHeaders == null ||
                stream.BehaviorHints.ProxyHeaders.Request == null)
            {
                return null;
            }

            Dictionary<string, string> headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> pair in stream.BehaviorHints.ProxyHeaders.Request)
            {
                if (headers.Count >= MaxRequestHeaders)
                {
                    break;
                }

                string name = pair.Key != null ? pair.Key.Trim() : null;
                string value = pair.Value != null ? pair.Value.Trim() : null;

                if (string.IsNullOrEmpty(name) ||
                    string.IsNullOrEmpty(value) ||
                    value.Length > MaxRequestHeaderValueLength ||
                    !HeaderNamePattern.IsMatch(name))
                {
                    continue;
                }

                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool hasControlCharacter = false;

                foreach (char c in value)
                {
                    if (char.IsControl(c))
                    {
                        hasControlCharacter = true;
                        break;
                    }
                }

                if (hasControlCharacter)
                {
                    continue;
                }

                headers[name] = value;
            }

            return headers.Count > 0 ? headers : null;
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
