using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;

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

        // TEMPORARY DIAGNOSTIC (tag [AGG-DIAG]): per-request timing around
        // the original aggregation body, which now lives unchanged in
        // AggregateCoreAsync. This wrapper only measures and logs; results,
        // exceptions and cancellation pass through exactly as before.
        // Remove together with AggregationDiagnostics once the slow stage
        // has been identified.
        private static int _diagSequence;

        public async Task<PlaybackSourceSet> AggregateAsync(ContentRequest request)
        {
            AggregationDiagnostics diag = new AggregationDiagnostics(
                Interlocked.Increment(ref _diagSequence));

            Stopwatch total = Stopwatch.StartNew();
            string outcome = "completed";

            DiagLog("[AGG-DIAG] Agg#{0} start", diag.Id);

            try
            {
                return await AggregateCoreAsync(request, diag);
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch (Exception ex)
            {
                outcome = "failed(" + ex.GetType().Name + ")";
                throw;
            }
            finally
            {
                total.Stop();

                DiagLog(
                    "[AGG-DIAG] Agg#{0} end outcome={1} totalMs={2} discoveryMs={3} " +
                    "streams={4} direct={5} torrentOnly={6} unsupported={7} nullEntries={8} " +
                    "rdDuplicatesSkipped={9} rdBudgetSkipped={10} rdAttempted={11} " +
                    "rdSucceeded={12} rdFailed={13} rdTotalMs={14} directResolved={15} " +
                    "sourcesReturned={16} unresolvedReturned={17}",
                    diag.Id,
                    outcome,
                    total.ElapsedMilliseconds,
                    diag.DiscoveryMs,
                    diag.Streams,
                    diag.Direct,
                    diag.TorrentOnly,
                    diag.Unsupported,
                    diag.NullEntries,
                    diag.RdDuplicatesSkipped,
                    diag.RdBudgetSkipped,
                    diag.RdAttempted,
                    diag.RdSucceeded,
                    diag.RdFailed,
                    diag.RdTotalMs,
                    diag.DirectResolved,
                    diag.SourcesReturned,
                    diag.UnresolvedReturned);
            }
        }

        // TEMPORARY DIAGNOSTIC: used only for [AGG-DIAG] messages. Formatting
        // and writing happen inside the try, so a logging failure can never
        // reach the aggregation path (results, exceptions, cancellation or
        // the existing per-stream failure handling). It is deliberately NOT
        // used for any production log line, and the aggregation logic's own
        // exceptions are never caught here.
        private static void DiagLog(string format, params object[] args)
        {
            try
            {
                StremioVideoCenterLogger.Info(string.Format(format, args));
            }
            catch (Exception)
            {
                // Diagnostic output is best-effort by design.
            }
        }

        private async Task<PlaybackSourceSet> AggregateCoreAsync(
            ContentRequest request,
            AggregationDiagnostics diag)
        {

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            PlaybackSourceSet result = new PlaybackSourceSet();


            Stopwatch discoveryWatch = Stopwatch.StartNew();

            List<StremioStream> streams = await _discovery.ResolveStreamsAsync(request);

            discoveryWatch.Stop();
            diag.DiscoveryMs = discoveryWatch.ElapsedMilliseconds;

            if (streams != null)
            {
                // [AGG-DIAG] counts by broad kind only (same classification
                // the loop below uses). Nothing from a stream is logged.
                diag.Streams = streams.Count;

                foreach (StremioStream counted in streams)
                {
                    if (counted == null)
                    {
                        diag.NullEntries++;
                    }
                    else if (StreamResolverService.IsPlayableDirectUrl(counted.Url))
                    {
                        diag.Direct++;
                    }
                    else if (!string.IsNullOrWhiteSpace(counted.InfoHash))
                    {
                        diag.TorrentOnly++;
                    }
                    else
                    {
                        diag.Unsupported++;
                    }
                }
            }

            DiagLog(
                "[AGG-DIAG] Agg#{0} discovery done ms={1} streams={2} direct={3} torrentOnly={4} unsupported={5}",
                diag.Id,
                diag.DiscoveryMs,
                diag.Streams,
                diag.Direct,
                diag.TorrentOnly,
                diag.Unsupported);

            if (streams == null || streams.Count == 0)
            {
                // Genuinely nothing found (no addon had anything, or the
                // request couldn't even build a Stremio id) - empty on both
                // sides, not an error.
                return result;
            }

            HashSet<string> seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenInfoHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int realDebridAttemptsRemaining = _maxRealDebridAttempts;

            foreach (StremioStream stream in streams)
            {
                if (stream == null)
                {
                    continue;
                }

                // Slice 22: a direct URL counts only when it is a well-formed
                // http(s) address (same rule the resolver applies). A stream
                // whose url is unusable but which has an infoHash is treated
                // as a torrent stream, including the budget and dedup below.
                bool hasDirectUrl = StreamResolverService.IsPlayableDirectUrl(stream.Url);
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
                        Reason = StreamResolverService.DescribeUnsupported(stream),
                        Cause = UnresolvedCause.Unsupported
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
                    // Deduplicate by InfoHash (+ FileIdx, since the same
                    // torrent can be a multi-file season pack where
                    // different FileIdx values are genuinely different
                    // episodes) before spending any Real-Debrid budget.
                    // Multiple addons - Torrentio and Comet in
                    // particular - frequently return the exact same
                    // torrent, and resolving the same InfoHash+FileIdx
                    // twice would burn two Real-Debrid resolutions (and
                    // two budget slots) on what is ultimately the same
                    // underlying file. First occurrence wins, same
                    // convention as the final Url-based dedup below - a
                    // later duplicate is skipped outright rather than
                    // resolved again. This never affects streams that
                    // also have a direct Url (never budgeted or
                    // InfoHash-deduplicated) or two torrents that merely
                    // share a title/hash-prefix but have distinct
                    // InfoHash+FileIdx combinations - those are
                    // legitimately distinct and both proceed.
                    string infoHashKey =
                        stream.InfoHash + "|" +
                        (stream.FileIdx.HasValue
                            ? stream.FileIdx.Value.ToString()
                            : "null");

                    if (!seenInfoHashes.Add(infoHashKey))
                    {
                        diag.RdDuplicatesSkipped++;
                        continue;
                    }

                    if (realDebridAttemptsRemaining <= 0)
                    {
                        diag.RdBudgetSkipped++;

                        result.Unresolved.Add(new UnresolvedSource
                        {
                            Stream = stream,
                            Reason = "Real-Debrid resolution limit reached (" +
                                _maxRealDebridAttempts +
                                " per request) - not attempted, retained for future selection.",
                            Cause = UnresolvedCause.NotAttempted
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

                string diagStreamIdentity =
                    !string.IsNullOrWhiteSpace(stream.Url)
                        ? "Url"
                        : (!string.IsNullOrWhiteSpace(stream.InfoHash)
                            ? "Torrent fileIdx=" +
                              (stream.FileIdx.HasValue ? stream.FileIdx.Value.ToString() : "null")
                            : "(neither)");

                // SECURITY: log the addon's FriendlyName only, never
                // SourceAddonUrl - per StremioAddonRegistry's own
                // remarks, a Real-Debrid-configured addon's URL can
                // carry the user's RD key embedded in it, and callers
                // must always fall back to a generic label instead of
                // the URL.

                // [AGG-DIAG] a torrent-only stream is the one that reaches
                // Real-Debrid; only those get per-attempt timing.
                bool diagIsRdAttempt = !hasDirectUrl && hasInfoHash;
                Stopwatch diagResolveWatch = Stopwatch.StartNew();
                int diagAttemptNumber = 0;

                if (diagIsRdAttempt)
                {
                    diag.RdAttempted++;
                    diagAttemptNumber = diag.RdAttempted;

                    DiagLog(
                        "[AGG-DIAG] Agg#{0} rd#{1} start",
                        diag.Id,
                        diagAttemptNumber);
                }

                try
                {
                    source = await _resolver.ResolveStreamAsync(stream);

                    diagResolveWatch.Stop();

                    if (diagIsRdAttempt)
                    {
                        diag.RdSucceeded++;
                        diag.RdTotalMs += diagResolveWatch.ElapsedMilliseconds;

                        DiagLog(
                            "[AGG-DIAG] Agg#{0} rd#{1} end outcome=ok ms={2}",
                            diag.Id,
                            diagAttemptNumber,
                            diagResolveWatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        diag.DirectResolved++;
                    }
                }
                catch (Exception ex)
                {
                    diagResolveWatch.Stop();

                    if (diagIsRdAttempt)
                    {
                        diag.RdFailed++;
                        diag.RdTotalMs += diagResolveWatch.ElapsedMilliseconds;

                        DiagLog(
                            "[AGG-DIAG] Agg#{0} rd#{1} end outcome={2} ms={3}",
                            diag.Id,
                            diagAttemptNumber,
                            ex is RealDebridException
                                ? ((RealDebridException)ex).Reason.ToString()
                                : ex.GetType().Name,
                            diagResolveWatch.ElapsedMilliseconds);
                    }

                    StremioVideoCenterLogger.Info(
                        string.Format(
                            "Stream resolution failed stream=[{0}] debridReason={1} ex={2}",
                            diagStreamIdentity,
                            ex is RealDebridException
                                ? ((RealDebridException)ex).Reason.ToString()
                                : "(n/a)",
                            UserFacingErrors.ForLog(ex)));

                    // One stream failing (Real-Debrid not configured, a
                    // specific torrent failing to resolve, a timeout, etc.)
                    // never fails the whole batch - recorded and skipped.
                    // Slice 20 Stage 2: the typed Real-Debrid reason (set
                    // only where the evidence established it) is kept so
                    // the caller can explain WHY nothing resolved. Reason
                    // stays the log-safe text and is never displayed.
                    RealDebridException debridFailure = ex as RealDebridException;

                    result.Unresolved.Add(new UnresolvedSource
                    {
                        Stream = stream,
                        Reason = UserFacingErrors.ForLog(ex),
                        Cause = ex is NotSupportedException
                            ? UnresolvedCause.Unsupported
                            : UnresolvedCause.ResolutionFailed,
                        DebridReason = debridFailure != null
                            ? (RealDebridFailureReason?)debridFailure.Reason
                            : null
                    });

                    continue;
                }

                if (source == null || string.IsNullOrWhiteSpace(source.Url))
                {
                    result.Unresolved.Add(new UnresolvedSource
                    {
                        Stream = stream,
                        Reason = "Resolved to an empty playback URL.",
                        Cause = UnresolvedCause.ResolutionFailed
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
                source.SourceAddonName = stream.SourceAddonName;
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

            diag.SourcesReturned = result.Sources.Count;
            diag.UnresolvedReturned = result.Unresolved.Count;

            return result;
        }

        // TEMPORARY DIAGNOSTIC: mutable counters for one AggregateAsync call.
        private sealed class AggregationDiagnostics
        {
            public AggregationDiagnostics(int id)
            {
                Id = id;
            }

            public readonly int Id;
            public long DiscoveryMs;
            public int Streams;
            public int Direct;
            public int TorrentOnly;
            public int Unsupported;
            public int NullEntries;
            public int RdDuplicatesSkipped;
            public int RdBudgetSkipped;
            public int RdAttempted;
            public int RdSucceeded;
            public int RdFailed;
            public long RdTotalMs;
            public int DirectResolved;
            public int SourcesReturned;
            public int UnresolvedReturned;
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

        // Log-safe diagnostic text. Not for display.
        public string Reason { get; set; }

        // Slice 20 Stage 2: why this stream is unresolved, as a category
        // (internal; user wording is UserFacingErrors').
        public UnresolvedCause Cause { get; set; }

        // Set only when resolution threw a classified Real-Debrid failure.
        public RealDebridFailureReason? DebridReason { get; set; }
    }

    public enum UnresolvedCause
    {
        // Resolution was attempted and failed (or produced nothing).
        ResolutionFailed,

        // Not resolvable in-app by design (e.g. external-only stream).
        Unsupported,

        // Skipped because the Real-Debrid attempt budget was used up.
        NotAttempted
    }
}
