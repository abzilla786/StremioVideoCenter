using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

            // Addon-role architecture (Slice 15 Anime Architecture Audit +
            // Kitsu Migration + Provider Role Correction) - see
            // AddonRole's remarks in AddonInstance.cs. Null/empty means
            // "no explicit role assigned" (every legacy/
            // FromConfiguredUrls entry, and any AddonInstance that never
            // set Roles) - GetAllStreamsAsync treats that the same way
            // it already treats an unknown ResourceNames list: fall back
            // to prior behaviour (query it, subject to the ResourceNames
            // check) rather than excluding it.
            public List<string> Roles;

            // The addon's stored manifest declarations (resources, types,
            // idPrefixes), when it was built from an AddonInstance. Null for
            // legacy URL-only entries. Used only to avoid asking an addon
            // for content ids it declared it does not serve.
            public AddonInstance Declaration;
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

                registry.AddAddon(instance.ManifestUrl, displayName, instance.ResourceNames, instance.Roles, instance);
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
        //
        // roles is optional too (defaults to null, meaning "no explicit
        // role assigned") - see AddonRole's remarks in AddonInstance.cs.
        // Only FromAddonInstances currently passes a non-empty value
        // (e.g. AnimeFLV's [Metadata, Catalogue]); every other caller -
        // including the legacy FromConfiguredUrls path, which has no
        // AddonInstance to read Roles from at all - gets an empty list,
        // which GetAllStreamsAsync's Roles-based check treats as
        // "unknown, fall back to the ResourceNames check", exactly
        // preserving "queried for every request it's capable of" prior
        // behaviour.
        public void AddAddon(string addonUrl, string friendlyName = null, List<string> resourceNames = null, List<string> roles = null, AddonInstance declaration = null)
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
                ResourceNames = resourceNames,
                Roles = roles,
                Declaration = declaration
            });
        }

        // Requests streams from every registered addon and aggregates the
        // results, tagging each stream with the addon URL it came from. An
        // addon that fails (unreachable, bad manifest, malformed response,
        // etc.) is skipped rather than failing the whole request - one
        // broken addon should never block streams from the others.
        // Slice 15 (Anime Architecture Audit + Kitsu Migration + Provider
        // Role Correction): no longer takes an isAnimeRequest flag. The
        // earlier Workstream B design scoped AnimeFLV's stream
        // eligibility to "only when the request is Anime" - explicitly
        // rejected by this slice: AnimeFLV must never be queried for
        // streams for ANY content type, unconditionally. Eligibility is
        // now decided purely by each addon's own Roles (see the
        // StreamProvider check below) - no content-type input is needed
        // at all, and none of this method's logic branches on the
        // request's content type.
        public async Task<List<StremioStream>> GetAllStreamsAsync(
            string type,
            string id,
            CancellationToken cancellationToken = default(CancellationToken))
        {


            List<Task<List<StremioStream>>> tasks = new List<Task<List<StremioStream>>>();

            // [STREAM-QUERY-DIAG] temporary, diagnostic only.
            int diagBatchId = Interlocked.Increment(ref _diagBatchSequence);
            Stopwatch diagBatchWatch = Stopwatch.StartNew();
            int diagSkippedNoStreamResource = 0;
            int diagSkippedRole = 0;
            int diagSkippedDeclaredId = 0;
            int diagQueried = 0;

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
                    diagSkippedNoStreamResource++;

                    continue;
                }

                // Addon-role skip (Slice 15 Anime Architecture Audit +
                // Kitsu Migration + Provider Role Correction): an addon
                // may genuinely declare "stream" capability in its
                // manifest (the check above does not exclude it) while
                // Zino still deliberately never wants it used for stream
                // discovery - AnimeFLV is the concrete case (Metadata +
                // Catalogue only, per its CoreAddonSeedingService
                // definition). Only an entry with a KNOWN, non-empty
                // Roles list that does NOT include StreamProvider is
                // skipped - unconditionally, for every content type,
                // with no isAnimeRequest/content-type input at all. An
                // entry with unknown Roles (null/empty - every legacy
                // FromConfiguredUrls entry, and any AddonInstance that
                // never set Roles, including Torrentio/Comet/custom
                // addons installed before this slice) falls through
                // unaffected, exactly preserving "queried for every
                // request it's capable of" prior behaviour.
                if (entry.Roles != null &&
                    entry.Roles.Count > 0 &&
                    !entry.Roles.Contains(AddonRole.StreamProvider))
                {
                    diagSkippedRole++;

                    continue;
                }

                // Declared-id skip (Slice 20 Stage 3 registration audit): an
                // addon whose manifest declares the id prefixes it serves for
                // "stream" (per-resource, else manifest-level) is not asked
                // for an id that matches none of them - e.g. an anime-only
                // provider is not sent a movie "tt..." id. Only the id prefix
                // is checked here: the type is deliberately not (passed as
                // null), because Zino sends anime as "series" and manifests
                // differ in how they list anime types. A manifest that
                // declares no prefixes, and every legacy entry, is unknown
                // and is still queried, exactly as before. Core addons are
                // exempt: their declarations are seeded by Zino rather than
                // read from a live manifest (Local Files, for one, is seeded
                // with only its own local:/bt: prefixes), so they are never
                // excluded by them.
                if (entry.Declaration != null &&
                    !entry.Declaration.IsCore &&
                    !AddonResourceCompatibility.AllowsContent(
                        entry.Declaration,
                        AddonCapabilities.Streams,
                        null,
                        id))
                {
                    diagSkippedDeclaredId++;

                    continue;
                }

                // Independent StreamProvider addons are queried
                // concurrently rather than sequentially (Slice 15
                // Workstream B) - a single slow/scraping-based provider
                // can no longer delay every other provider's results.
                // Each addon's own request remains fully independent -
                // its own try/catch, its own timing/logging, and its own
                // SourceAddonUrl/SourceAddonName tagging, all unchanged
                // from before this slice and still performed inside
                // QueryAddonAsync below - only WHEN the requests are
                // issued changes, not any per-addon behaviour.
                // [STREAM-QUERY-DIAG] hands the batch id / query number to
                // QueryAddonAsync and StremioAddonService through an
                // AsyncLocal, so no signature changes. Diagnostic only.
                diagQueried++;
                StremioAddonService.DiagContext.Value =
                    new StremioAddonService.DiagQueryContext(diagBatchId, diagQueried, diagBatchWatch);

                tasks.Add(QueryAddonAsync(entry, type, id, cancellationToken));
            }

            StremioAddonService.DiagContext.Value = null;

            DiagLog(
                "[STREAM-QUERY-DIAG] B{0} batch start registered={1} queried={2} " +
                "skippedNoStreamResource={3} skippedRole={4} skippedDeclaredId={5} elapsedMs={6}",
                diagBatchId,
                _entries.Count,
                diagQueried,
                diagSkippedNoStreamResource,
                diagSkippedRole,
                diagSkippedDeclaredId,
                diagBatchWatch.ElapsedMilliseconds);

            List<StremioStream>[] perAddonResults = null;
            string diagBatchOutcome = "threw";

            try
            {
                perAddonResults = await Task.WhenAll(tasks);
                diagBatchOutcome = "completed";
            }
            finally
            {
                DiagLog(
                    "[STREAM-QUERY-DIAG] B{0} batch end outcome={1} queried={2} elapsedMs={3}",
                    diagBatchId,
                    diagBatchOutcome,
                    diagQueried,
                    diagBatchWatch.ElapsedMilliseconds);
            }

            List<StremioStream> combined = new List<StremioStream>();

            foreach (List<StremioStream> streams in perAddonResults)
            {
                if (streams != null)
                {
                    combined.AddRange(streams);
                }
            }


            return combined;
        }

        // TEMPORARY DIAGNOSTIC ([STREAM-QUERY-DIAG]): batch counter, a safe
        // logging helper, and a safe addon label. Formatting and writing
        // happen inside the try, so a logging failure can never reach the
        // discovery path. Used for no other log line.
        private static int _diagBatchSequence;

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

        private static string SafeDiagLabel(AddonEntry entry)
        {
            try
            {
                return AddonLogName(entry);
            }
            catch (Exception)
            {
                return "(unknown)";
            }
        }

        // Name used in diagnostics. Normally the addon's friendly name. A
        // friendly name that is itself an http(s) URL (an addon added by
        // URL and never renamed) is reduced to its host name only: such a
        // URL can carry the user's key or addon configuration in its path
        // or query, none of which may reach the log.
        private static string AddonLogName(AddonEntry entry)
        {
            return StremioAddonService.SafeLogName(entry.FriendlyName);
        }

        // Extracted from GetAllStreamsAsync's old inline sequential loop
        // body so each eligible addon's request can be launched
        // concurrently via Task.WhenAll above. The try/catch, logging,
        // and SourceAddonUrl/SourceAddonName tagging below are otherwise
        // completely unchanged from before this slice - including
        // unconditionally adding a null stream entry to the returned
        // list exactly as the original inline loop did (only the tagging
        // assignment was ever conditional on non-null), since downstream
        // consumers (PlaybackSourceAggregationService.AggregateAsync)
        // already guard against a null stream entry.
        private async Task<List<StremioStream>> QueryAddonAsync(
            AddonEntry entry,
            string type,
            string id,
            CancellationToken cancellationToken)
        {
            DateTime diagAddonCallStart = DateTime.UtcNow;

            // [STREAM-QUERY-DIAG] read synchronously, before the first await,
            // so it is this addon's own context. Diagnostic only.
            StremioAddonService.DiagQueryContext diagCtx = StremioAddonService.DiagContext.Value;
            Stopwatch diagQueryWatch = Stopwatch.StartNew();
            long diagStartOffsetMs = diagCtx != null ? diagCtx.BatchWatch.ElapsedMilliseconds : -1;
            string diagLabel = SafeDiagLabel(entry);


            try
            {
                List<StremioStream> streams = await entry.Service.GetStreamsAsync(type, id, cancellationToken);

                DiagLog(
                    "[STREAM-QUERY-DIAG] {0} addon={1} startOffsetMs={2} elapsedMs={3} outcome=success items={4}",
                    diagCtx != null ? "B" + diagCtx.BatchId + ".Q" + diagCtx.QueryNumber : "(none)",
                    diagLabel,
                    diagStartOffsetMs,
                    diagQueryWatch.ElapsedMilliseconds,
                    streams != null ? streams.Count : 0);


                List<StremioStream> tagged = new List<StremioStream>();

                if (streams != null)
                {
                    foreach (StremioStream stream in streams)
                    {
                        if (stream != null)
                        {
                            stream.SourceAddonUrl = entry.Url;
                            stream.SourceAddonName = entry.FriendlyName;
                        }

                        tagged.Add(stream);
                    }
                }

                return tagged;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Only the CALLER's cancellation is rethrown - it is not an
                // addon failure and must not be swallowed as one. Any other
                // cancellation-type exception (not caused by this token)
                // falls through to the general handler below and is treated
                // as that addon failing, exactly as before.
                DiagLog(
                    "[STREAM-QUERY-DIAG] {0} addon={1} startOffsetMs={2} elapsedMs={3} outcome=cancelled",
                    diagCtx != null ? "B" + diagCtx.BatchId + ".Q" + diagCtx.QueryNumber : "(none)",
                    diagLabel,
                    diagStartOffsetMs,
                    diagQueryWatch.ElapsedMilliseconds);

                throw;
            }
            catch (Exception ex)
            {
                DiagLog(
                    "[STREAM-QUERY-DIAG] {0} addon={1} startOffsetMs={2} elapsedMs={3} outcome={4} exceptionType={5}",
                    diagCtx != null ? "B" + diagCtx.BatchId + ".Q" + diagCtx.QueryNumber : "(none)",
                    diagLabel,
                    diagStartOffsetMs,
                    diagQueryWatch.ElapsedMilliseconds,
                    ex is TimeoutException
                        ? "timeout"
                        : (ex is OperationCanceledException ? "cancelled(not-caller)" : "failed"),
                    ex.GetType().Name);

                // Slice 20 Stage 1A: ForLog (exception type + message with
                // URLs and local paths removed) instead of the raw message,
                // which can carry the addon URL and its embedded key.
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "Addon stream query failed addon={0} elapsedMs={1} ex={2}",
                        AddonLogName(entry),
                        (DateTime.UtcNow - diagAddonCallStart).TotalMilliseconds,
                        UserFacingErrors.ForLog(ex)));

                // Skip this addon; others may still succeed - same
                // fail-soft contract as before this slice, just returning
                // an empty list instead of relying on the caller's shared
                // "combined" accumulator never being touched.
                return new List<StremioStream>();
            }
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
                    result.Message = UserFacingErrors.ForLog(ex);
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
