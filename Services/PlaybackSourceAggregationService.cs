using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Turns a ContentRequest into a set of candidate PlaybackSources, rather
    // than the "stop at the first stream that resolves" policy that still
    // lives inline in PlaySelectedMediaAsync. This is the reusable,
    // testable version of that same idea: try every discovered stream,
    // keep every success (deduplicated), and keep enough about every
    // failure that a future source-selection UI can still show it rather
    // than it silently vanishing.
    //
    // Pipeline: ContentRequest -> StremioContentDiscoveryService (all
    // configured addons, aggregated, SourceAddonUrl-tagged) ->
    // StreamResolverService (per stream: Direct or Real-Debrid) ->
    // PlaybackSourceSet (ordered, deduplicated).
    //
    // Produces data only - never touches MPV, PlayerSession, or any player
    // file, and never picks a "best" source itself. That decision belongs
    // to whatever calls this (today: nothing does yet - proven via the
    // Settings test button; a future source-selection UI is the intended
    // real caller).
    public class PlaybackSourceAggregationService
    {
        // Defensive ceiling on torrent (InfoHash) resolution attempts per
        // AggregateAsync call - a real addon can return dozens of torrent
        // candidates, and resolving every one sequentially through
        // Real-Debrid (add magnet -> poll -> select files -> poll ->
        // unrestrict) has no natural upper bound on latency or API calls.
        // Direct-URL streams are never subject to this - they cost nothing
        // external, so there's nothing to bound.
        //
        // Worst-case latency at this default, derived directly from
        // RealDebridService's own polling parameters (15 attempts x 1s +
        // 30 attempts x 2s = 75s of polling delay per torrent, before any
        // HTTP round-trip time): 5 sequential uncached worst-case attempts
        // is approximately 5 x 75s = 375s (6 minutes 15 seconds) of polling
        // delay alone, plus HTTP overhead on top of that (roughly 48 HTTP
        // calls per torrent in the true worst case - the 15+30 polling GETs
        // plus the addMagnet/selectFiles/unrestrict POSTs). In practice,
        // cached torrents resolve in a couple of seconds - this figure is
        // the defensive ceiling being bounded against, not the expected
        // typical latency.
        public const int DefaultMaxRealDebridAttempts = 5;

        private readonly StremioContentDiscoveryService _discovery;
        private readonly StreamResolverService _resolver;
        private readonly int _maxRealDebridAttempts;

        public PlaybackSourceAggregationService(
            StremioContentDiscoveryService discovery,
            StreamResolverService resolver,
            int maxRealDebridAttempts = DefaultMaxRealDebridAttempts)
        {
            if (discovery == null)
            {
                throw new ArgumentNullException(nameof(discovery));
            }

            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            if (maxRealDebridAttempts < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRealDebridAttempts));
            }

            _discovery = discovery;
            _resolver = resolver;
            _maxRealDebridAttempts = maxRealDebridAttempts;
        }

        public async Task<PlaybackSourceSet> AggregateAsync(ContentRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            PlaybackSourceSet result = new PlaybackSourceSet();

            List<StremioStream> streams = await _discovery.ResolveStreamsAsync(request);

            if (streams == null || streams.Count == 0)
            {
                // Genuinely nothing found (no addon had anything, or the
                // request couldn't even build a Stremio id) - empty on both
                // sides, not an error.
                return result;
            }

            HashSet<string> seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int realDebridAttemptsRemaining = _maxRealDebridAttempts;

            foreach (StremioStream stream in streams)
            {
                if (stream == null)
                {
                    continue;
                }

                bool hasDirectUrl = !string.IsNullOrWhiteSpace(stream.Url);
                bool hasInfoHash = !string.IsNullOrWhiteSpace(stream.InfoHash);

                // ExternalUrl-only (or genuinely empty) streams are
                // categorically unsupported for in-app playback - the same
                // rule StreamResolverService itself enforces - but their
                // metadata is still preserved here rather than silently
                // dropped, per "don't silently discard useful metadata
                // before the future source-selection UI."
                if (!hasDirectUrl && !hasInfoHash)
                {
                    result.Unresolved.Add(new UnresolvedSource
                    {
                        Stream = stream,
                        Reason = "No direct URL or torrent source - external-only streams are not supported for in-app playback."
                    });

                    continue;
                }

                // Torrent-only streams (no direct URL) consume the bounded
                // Real-Debrid budget for this request. A stream with BOTH a
                // direct Url and an InfoHash is never budgeted - it's free,
                // since StreamResolverService always uses Url first and
                // never touches Real-Debrid for it regardless of InfoHash
                // being present too.
                if (!hasDirectUrl && hasInfoHash)
                {
                    if (realDebridAttemptsRemaining <= 0)
                    {
                        result.Unresolved.Add(new UnresolvedSource
                        {
                            Stream = stream,
                            Reason = "Real-Debrid resolution limit reached (" +
                                _maxRealDebridAttempts +
                                " per request) - not attempted, retained for future selection."
                        });

                        continue;
                    }

                    // Decremented on attempt, not on success - a failed
                    // resolution still cost a real Real-Debrid round-trip
                    // and real wall-clock time, which is exactly what this
                    // budget exists to bound.
                    realDebridAttemptsRemaining--;
                }

                PlaybackSource source;

                try
                {
                    source = await _resolver.ResolveStreamAsync(stream);
                }
                catch (Exception ex)
                {
                    // One stream failing (Real-Debrid not configured, a
                    // specific torrent failing to resolve, a timeout, etc.)
                    // never fails the whole batch - recorded and skipped.
                    result.Unresolved.Add(new UnresolvedSource
                    {
                        Stream = stream,
                        Reason = ex.Message
                    });

                    continue;
                }

                if (source == null || string.IsNullOrWhiteSpace(source.Url))
                {
                    result.Unresolved.Add(new UnresolvedSource
                    {
                        Stream = stream,
                        Reason = "Resolved to an empty playback URL."
                    });

                    continue;
                }

                // source.Kind was already set correctly by
                // StreamResolverService itself (the only place with actual
                // ground truth about which branch it took) - not inferred
                // here from stream shape, which is exactly what could
                // mislabel a stream that has both Url and InfoHash.
                source.Name = stream.Name;
                source.SourceAddonUrl = stream.SourceAddonUrl;
                source.OriginatingStream = stream;

                // Deduplicate genuinely identical playback sources: the same
                // final resolved URL means the same underlying video
                // regardless of which addon/stream entry produced it (e.g.
                // two addons returning the same direct link, or the same
                // addon configured twice). First occurrence wins, which
                // preserves discovery order - never conflate two different
                // URLs just because their titles look similar.
                if (!seenUrls.Add(source.Url))
                {
                    continue;
                }

                result.Sources.Add(source);
            }

            return result;
        }
    }

    // =============================================================
    // PLAYBACK SOURCE SET
    // =============================================================

    // The Slice 4 output: candidate playback sources ready for MPV (in
    // discovery order, deduplicated), plus enough about what didn't
    // resolve to inform a future source-selection UI without re-querying
    // anything.
    public class PlaybackSourceSet
    {
        public PlaybackSourceSet()
        {
            Sources = new List<PlaybackSource>();
            Unresolved = new List<UnresolvedSource>();
        }

        public List<PlaybackSource> Sources { get; set; }

        public List<UnresolvedSource> Unresolved { get; set; }
    }

    // =============================================================
    // UNRESOLVED SOURCE
    // =============================================================

    // A stream that could not become a PlaybackSource this phase - kept,
    // not discarded, so a future source-selection UI can still show it
    // ("this source needs Real-Debrid", "this source hit the resolution
    // limit", "this source failed to resolve") instead of it just
    // vanishing.
    public class UnresolvedSource
    {
        public StremioStream Stream { get; set; }

        public string Reason { get; set; }
    }
}
