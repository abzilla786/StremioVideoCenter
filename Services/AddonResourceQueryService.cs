using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // ADDON QUERY OUTCOME (Slice 20 Stage 1B)
    // =============================================================
    //
    // What happened when one addon was asked for one resource. Kept
    // distinct on purpose: an addon that answered with nothing is not a
    // failed addon, a slow addon is not a broken one, and the caller
    // cancelling is never reported as an addon problem.
    public enum AddonQueryOutcome
    {
        SuccessWithResults,
        SuccessNoResults,
        Failed,
        TimedOut,
        Skipped,
        Cancelled
    }

    // The result for one addon. AddonName is a display/diagnostic-safe
    // name (see StremioAddonService.SafeLogName) - never a URL. Reason is
    // a short fixed phrase and never carries exception text, a URL or a
    // response body.
    public sealed class AddonQueryResult<T>
    {
        public string AddonName { get; set; }

        public AddonQueryOutcome Outcome { get; set; }

        public List<T> Items { get; set; } = new List<T>();

        public string Reason { get; set; }

        public long ElapsedMs { get; set; }
    }

    // =============================================================
    // ADDON RESOURCE QUERY (Slice 20 Stage 1B)
    // =============================================================
    //
    // Generic, bounded query of one non-stream resource across the
    // addon instances that hold a given role. The first consumer is
    // subtitle discovery (SubtitleDiscoveryService).
    //
    // This does NOT replace or wrap stream discovery: that path
    // (StremioAddonRegistry.GetAllStreamsAsync) is unchanged.
    //
    // There is no addon-specific code here. Which addons are asked is
    // decided by role, enabled state and what each manifest declared;
    // how a response is read is supplied by the caller as a parser, so a
    // provider with a different response schema only needs its own
    // parser.
    //
    // Each addon is queried independently (own timeout, own outcome), at
    // most maxConcurrency at a time. One addon failing, timing out or
    // returning garbage never affects the others. Nothing here logs a
    // URL, credential, addon configuration or response body.
    public static class AddonResourceQuery
    {
        public const int DefaultTimeoutMs = 15000;

        public const int DefaultMaxConcurrency = 4;

        public static async Task<List<AddonQueryResult<T>>> QueryAsync<T>(
            IEnumerable<AddonInstance> addons,
            string resource,
            string requiredRole,
            string type,
            string id,
            Func<string, List<T>> parseResponse,
            int timeoutMs,
            int maxConcurrency,
            CancellationToken cancellationToken)
        {
            if (parseResponse == null)
            {
                throw new ArgumentNullException(nameof(parseResponse));
            }

            if (addons == null)
            {
                return new List<AddonQueryResult<T>>();
            }

            // Only addons that hold the role are part of this query at
            // all; the rest are not "skipped", they are simply not
            // candidates (a streaming addon is not a subtitle candidate).
            List<AddonInstance> candidates = addons
                .Where(addon =>
                    addon != null &&
                    (string.IsNullOrWhiteSpace(requiredRole) ||
                     (addon.Roles != null && addon.Roles.Contains(requiredRole))))
                .ToList();

            using (SemaphoreSlim gate = new SemaphoreSlim(Math.Max(1, maxConcurrency)))
            {
                List<Task<AddonQueryResult<T>>> tasks = candidates
                    .Select(addon => QueryOneAsync(
                        addon, resource, type, id, parseResponse, timeoutMs, gate, cancellationToken))
                    .ToList();

                AddonQueryResult<T>[] results = await Task.WhenAll(tasks);

                return results.ToList();
            }
        }

        // Why an addon is not asked at all, or null when it should be.
        // Unknown restrictions (no per-resource or manifest-level
        // types/idPrefixes) never produce a skip - see
        // AddonResourceCompatibility.
        internal static string GetSkipReason(
            AddonInstance addon,
            string resource,
            string type,
            string id)
        {
            if (!addon.Enabled)
            {
                return "Disabled.";
            }

            if (string.IsNullOrWhiteSpace(addon.ManifestUrl))
            {
                return "No addon address.";
            }

            if (addon.ConfigurationRequired)
            {
                return "Configuration required.";
            }

            // A known resource list that does not include the resource
            // means the addon cannot serve it. An unknown (empty) list is
            // "capability unknown" and is not a reason to skip.
            if (AddonCapabilities.HasKnownCapabilities(addon) &&
                !AddonResourceCompatibility.Declares(addon, resource))
            {
                return "Resource not declared by the addon.";
            }

            if (!AddonResourceCompatibility.AllowsContent(addon, resource, type, id))
            {
                return "Content type or id not supported by the addon.";
            }

            return null;
        }

        private static async Task<AddonQueryResult<T>> QueryOneAsync<T>(
            AddonInstance addon,
            string resource,
            string type,
            string id,
            Func<string, List<T>> parseResponse,
            int timeoutMs,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            AddonQueryResult<T> result = new AddonQueryResult<T>
            {
                AddonName = StremioAddonService.SafeLogName(addon.FriendlyName)
            };

            string skipReason = GetSkipReason(addon, resource, type, id);

            if (skipReason != null)
            {
                result.Outcome = AddonQueryOutcome.Skipped;
                result.Reason = skipReason;

                LogResult(resource, result, null);

                return result;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool acquired = false;
            string diagnostic = null;

            try
            {
                await gate.WaitAsync(cancellationToken);
                acquired = true;

                StremioAddonService service = new StremioAddonService(addon.ManifestUrl);

                string json = await service.GetResourceJsonAsync(
                    resource, type, id, timeoutMs, cancellationToken);

                List<T> items = parseResponse(json) ?? new List<T>();

                result.Items = items;

                result.Outcome = items.Count > 0
                    ? AddonQueryOutcome.SuccessWithResults
                    : AddonQueryOutcome.SuccessNoResults;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled: not an addon failure.
                result.Outcome = AddonQueryOutcome.Cancelled;
                result.Reason = "Cancelled.";
            }
            catch (TimeoutException)
            {
                result.Outcome = AddonQueryOutcome.TimedOut;
                result.Reason = "Timed out.";
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
            {
                result.Outcome = AddonQueryOutcome.TimedOut;
                result.Reason = "Timed out.";
            }
            catch (Exception ex)
            {
                // Network/HTTP error, or a response the parser could not
                // read. Exception text is kept for the log only, with URLs
                // and local paths removed, and never reaches the result.
                result.Outcome = AddonQueryOutcome.Failed;
                result.Reason = "Request failed.";
                diagnostic = UserFacingErrors.ForLog(ex);
            }
            finally
            {
                if (acquired)
                {
                    gate.Release();
                }

                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            }

            LogResult(resource, result, diagnostic);

            return result;
        }

        private static void LogResult<T>(
            string resource,
            AddonQueryResult<T> result,
            string diagnostic)
        {
            StremioVideoCenterLogger.Info(
                string.Format(
                    "[ADDON-QUERY] resource={0} addon={1} outcome={2} items={3} elapsedMs={4}{5}{6}",
                    resource,
                    result.AddonName,
                    result.Outcome,
                    result.Items != null ? result.Items.Count : 0,
                    result.ElapsedMs,
                    string.IsNullOrEmpty(result.Reason) ? string.Empty : " reason=" + result.Reason,
                    string.IsNullOrEmpty(diagnostic) ? string.Empty : " ex=" + diagnostic));
        }
    }
}
