using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Talks to the Real-Debrid REST API only. Knows nothing about Stremio,
    // MPV, or the player - StreamResolverService is the only thing that
    // calls this, and only when a stream has no direct Url of its own.
    //
    // Uses HttpClient + DataContractJsonSerializer (Bearer auth, 15s-ish
    // timeouts) rather than introducing Newtonsoft into this layer -
    // Newtonsoft is already used by the player's MpvIpcClient, but the
    // metadata/addon layer (StremioAddonService, KitsuService) has
    // consistently used DataContractJsonSerializer,
    // and this keeps that layer internally consistent instead of mixing two
    // JSON frameworks in the same architectural tier.
    public class RealDebridService
    {
        private const string BaseUrl = "https://api.real-debrid.com/rest/1.0/";

        private readonly string accessToken;

        // TEMPORARY DIAGNOSTIC (Part 2, controller-A-delay investigation)
        // - sequence counter for the RD# log lines below. Purely
        // observational - no behaviour here is changed. Never used to
        // log accessToken or any URL that could carry it.
        private static int _diagResolveMagnetSeq;

        public RealDebridService(string accessToken)
        {
            this.accessToken = accessToken;
        }

        // =========================================================
        // TEST CONNECTION
        // =========================================================

        public async Task<bool> TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            try
            {
                string json = await GetAsync("user");
                return !string.IsNullOrWhiteSpace(json);
            }
            catch
            {
                return false;
            }
        }

        // =========================================================
        // RESOLVE A MAGNET (InfoHash [+ FileIdx]) TO A DIRECT URL
        // =========================================================
        //
        // Flow: add magnet -> select file(s) -> wait for the torrent to
        // finish (cached torrents resolve almost immediately; uncached ones
        // can take a while or never finish if nobody is seeding) -> unrestrict
        // the resulting host link into a direct HTTPS URL.
        //
        // fileIdx, when present, comes from Stremio and is 0-based; Real-Debrid's
        // own file selection is 1-based.

        public async Task<string> ResolveMagnetAsync(string infoHash, int? fileIdx)
        {
            int diagSeq = Interlocked.Increment(ref _diagResolveMagnetSeq);
            DateTime diagMethodStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} begin fileIdx={2}",
                    diagSeq,
                    diagMethodStart,
                    fileIdx.HasValue ? fileIdx.Value.ToString() : "null"));

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new RealDebridException(
                    RealDebridFailureReason.NotConfigured,
                    "Real-Debrid API token has not been configured.");
            }

            if (string.IsNullOrWhiteSpace(infoHash))
            {
                throw new ArgumentException("infoHash cannot be empty.", nameof(infoHash));
            }

            string magnet = "magnet:?xt=urn:btih:" + infoHash;

            DateTime diagStepStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} awaiting POST torrents/addMagnet",
                    diagSeq,
                    diagStepStart));

            RealDebridAddMagnetResponse added = await PostFormAsync<RealDebridAddMagnetResponse>(
                "torrents/addMagnet",
                new Dictionary<string, string> { { "magnet", magnet } });

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} addMagnet returned elapsedMs={2} hasTorrentId={3}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagStepStart).TotalMilliseconds,
                    added != null && !string.IsNullOrWhiteSpace(added.Id)));

            if (added == null || string.IsNullOrWhiteSpace(added.Id))
            {
                // The request itself succeeded but the reply was not usable.
                throw new RealDebridException(
                    RealDebridFailureReason.UnexpectedResponse,
                    "Real-Debrid did not return a torrent id for this magnet.");
            }

            diagStepStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} awaiting WaitForStatusAsync #1 (maxAttempts=15, delayMs=1000, waiting_files_selection|downloaded)",
                    diagSeq,
                    diagStepStart));

            await WaitForStatusAsync(
                added.Id,
                maxAttempts: 15,
                delayMilliseconds: 1000,
                acceptableStatuses: new[] { "waiting_files_selection", "downloaded" },
                diagLabel: "RD#" + diagSeq + " poll1");

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} WaitForStatusAsync #1 returned elapsedMs={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagStepStart).TotalMilliseconds));

            string fileSelection = fileIdx.HasValue
                ? (fileIdx.Value + 1).ToString()
                : "all";

            diagStepStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} awaiting POST torrents/selectFiles fileSelection={2}",
                    diagSeq,
                    diagStepStart,
                    fileSelection));

            await PostFormAsync<object>(
                "torrents/selectFiles/" + added.Id,
                new Dictionary<string, string> { { "files", fileSelection } });

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} selectFiles returned elapsedMs={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagStepStart).TotalMilliseconds));

            diagStepStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} awaiting WaitForStatusAsync #2 (maxAttempts=30, delayMs=2000, downloaded)",
                    diagSeq,
                    diagStepStart));

            RealDebridTorrentInfo info = await WaitForStatusAsync(
                added.Id,
                maxAttempts: 30,
                delayMilliseconds: 2000,
                acceptableStatuses: new[] { "downloaded" },
                diagLabel: "RD#" + diagSeq + " poll2");

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} WaitForStatusAsync #2 returned elapsedMs={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagStepStart).TotalMilliseconds));

            if (info.Links == null || info.Links.Count == 0)
            {
                // A successful, finished torrent with no link: a valid
                // response with no playable result, not a failed request.
                throw new RealDebridException(
                    RealDebridFailureReason.NoPlayableResult,
                    "Real-Debrid did not return a downloadable link for this torrent.");
            }

            string hostLink = info.Links[0];

            diagStepStart = DateTime.UtcNow;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} awaiting POST unrestrict/link",
                    diagSeq,
                    diagStepStart));

            RealDebridUnrestrictResponse unrestricted = await PostFormAsync<RealDebridUnrestrictResponse>(
                "unrestrict/link",
                new Dictionary<string, string> { { "link", hostLink } });

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} unrestrict/link returned elapsedMs={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagStepStart).TotalMilliseconds));

            if (unrestricted == null || string.IsNullOrWhiteSpace(unrestricted.Download))
            {
                // A successful reply that carried no download URL.
                throw new RealDebridException(
                    RealDebridFailureReason.NoPlayableResult,
                    "Real-Debrid was unable to unrestrict this link.");
            }

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] RD#{0} time={1:HH:mm:ss.fff} end totalElapsedMs={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (DateTime.UtcNow - diagMethodStart).TotalMilliseconds));

            return unrestricted.Download;
        }

        // =========================================================
        // POLL TORRENT STATUS
        // =========================================================
        //
        // Bounded, best-effort polling. These attempt/delay defaults are
        // reasonable starting points, not values verified against real
        // Real-Debrid account behaviour - they may need tuning once this is
        // tested against real magnets.

        private async Task<RealDebridTorrentInfo> WaitForStatusAsync(
            string torrentId,
            int maxAttempts,
            int delayMilliseconds,
            string[] acceptableStatuses,
            string diagLabel = null)
        {
            DateTime diagPollStart = DateTime.UtcNow;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                DateTime diagAttemptStart = DateTime.UtcNow;

                RealDebridTorrentInfo info = await GetTorrentInfoAsync(torrentId);

                // DIAGNOSTIC-ONLY (Part 2, controller-A-delay
                // investigation): this loop, per the aggregation
                // service's own documented worst case (15x1s + 30x2s =
                // 75s per torrent), is the single most likely site of
                // multi-second delay in the whole pipeline - logging
                // every attempt (not just entry/exit) is deliberate here.
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] {0} time={1:HH:mm:ss.fff} attempt={2}/{3} elapsedSinceLoopStartMs={4} status={5} attemptElapsedMs={6}",
                        diagLabel ?? "RD-poll",
                        DateTime.UtcNow,
                        attempt + 1,
                        maxAttempts,
                        (DateTime.UtcNow - diagPollStart).TotalMilliseconds,
                        info != null ? info.Status : "(null info)",
                        (DateTime.UtcNow - diagAttemptStart).TotalMilliseconds));

                if (info == null)
                {
                    throw new RealDebridException(
                        RealDebridFailureReason.UnexpectedResponse,
                        "Real-Debrid returned no torrent info.");
                }

                foreach (string status in acceptableStatuses)
                {
                    if (string.Equals(info.Status, status, StringComparison.OrdinalIgnoreCase))
                    {
                        return info;
                    }
                }

                if (IsErrorStatus(info.Status))
                {
                    // The status value itself is already in the poll log
                    // line above; it is not repeated in the message.
                    throw new RealDebridException(
                        RealDebridFailureReason.ResourceUnavailable,
                        "Real-Debrid reported that it could not process this torrent.");
                }

                await Task.Delay(delayMilliseconds);
            }

            // The polling budget above is unchanged. Running out of it is
            // not a network timeout: every request answered, the torrent
            // just never reached the awaited status in time.
            throw new RealDebridException(
                RealDebridFailureReason.NotReadyInTime,
                "Timed out waiting for Real-Debrid to process this torrent. It may not be cached.");
        }

        private static bool IsErrorStatus(string status)
        {
            return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "magnet_error", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "virus", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "dead", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<RealDebridTorrentInfo> GetTorrentInfoAsync(string torrentId)
        {
            string json = await GetAsync("torrents/info/" + torrentId);
            return Deserialize<RealDebridTorrentInfo>(json);
        }

        // =========================================================
        // HTTP GET (Bearer auth)
        // =========================================================

        private async Task<string> GetAsync(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new RealDebridException(
                    RealDebridFailureReason.NotConfigured,
                    "Real-Debrid API token has not been configured.");
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri(BaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(15);

                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));

                    using (var response = await client.GetAsync(endpoint))
                    {
                        ThrowIfNotSuccess(response);
                        return await response.Content.ReadAsStringAsync();
                    }
                }
            }
            catch (RealDebridException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw CreateTransportException(ex);
            }
        }

        // =========================================================
        // HTTP POST (form-encoded, Bearer auth)
        // =========================================================

        private async Task<T> PostFormAsync<T>(string endpoint, Dictionary<string, string> form)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new RealDebridException(
                    RealDebridFailureReason.NotConfigured,
                    "Real-Debrid API token has not been configured.");
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.BaseAddress = new Uri(BaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);

                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                    using (var content = new FormUrlEncodedContent(form))
                    using (var response = await client.PostAsync(endpoint, content))
                    {
                        ThrowIfNotSuccess(response);

                        string json = await response.Content.ReadAsStringAsync();
                        return Deserialize<T>(json);
                    }
                }
            }
            catch (RealDebridException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw CreateTransportException(ex);
            }
        }

        // =========================================================
        // FAILURE CLASSIFICATION
        // =========================================================
        //
        // Replaces HttpResponseMessage.EnsureSuccessStatusCode, which
        // threw a generic HttpRequestException whose only record of the
        // status was its message text. The HTTP status code is the one
        // structured signal Real-Debrid's REST API gives for these
        // reasons, so it is read directly. The response BODY is never
        // read, stored, logged or put in an exception: only the numeric
        // status and a fixed message.
        //
        // Request behaviour (endpoints, headers, timeouts, ordering) is
        // unchanged; only what is thrown on failure differs.

        private static void ThrowIfNotSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            int code = (int)response.StatusCode;

            RealDebridFailureReason reason;

            if (code == 401 || code == 403)
            {
                // 401: bad/expired token. 403: permission denied (e.g. a
                // locked or non-premium account). Both are the credential
                // or account being refused.
                reason = RealDebridFailureReason.Authentication;
            }
            else if (code == 429)
            {
                reason = RealDebridFailureReason.RateLimited;
            }
            else if (code == 404)
            {
                reason = RealDebridFailureReason.ResourceUnavailable;
            }
            else if (code == 500 || code == 502 || code == 503 || code == 504)
            {
                reason = RealDebridFailureReason.ServiceFailure;
            }
            else
            {
                // Any other non-success status (e.g. 400): a rejected
                // request whose cause this layer cannot establish.
                reason = RealDebridFailureReason.Rejected;
            }

            throw new RealDebridException(
                reason,
                "Real-Debrid returned HTTP " + code + ".",
                code);
        }

        // Only failures of the transport itself. No cancellation token is
        // ever passed to these HttpClient calls, so a TaskCanceledException
        // can only be HttpClient's own timeout.
        private static bool IsTransportFailure(Exception ex)
        {
            return ex is TaskCanceledException ||
                   ex is HttpRequestException ||
                   ex is IOException;
        }

        private static RealDebridException CreateTransportException(Exception ex)
        {
            if (ex is TaskCanceledException)
            {
                return new RealDebridException(
                    RealDebridFailureReason.Timeout,
                    "The request to Real-Debrid timed out.");
            }

            return new RealDebridException(
                RealDebridFailureReason.Network,
                "Could not reach Real-Debrid.");
        }

        // =========================================================
        // JSON DESERIALIZATION
        // =========================================================

        private T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default(T);
            }

            var serializer = new DataContractJsonSerializer(typeof(T));

            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (SerializationException)
            {
                // The request succeeded but the reply could not be read.
                // (The exception text is dropped: it can quote the body.)
                throw new RealDebridException(
                    RealDebridFailureReason.UnexpectedResponse,
                    "Real-Debrid returned a reply that could not be read.");
            }
        }
    }

    // =============================================================
    // FAILURE REASON (Slice 20 Stage 2)
    // =============================================================
    //
    // Internal diagnostic category for a failed Real-Debrid resolution.
    // Never shown to the user directly: the wording lives in
    // UserFacingErrors. Each value is set only where the evidence
    // establishes it (an HTTP status, a reliable exception type, or a
    // specific structural condition in a successful reply); anything else
    // is Rejected / Unknown rather than a guessed cause.
    public enum RealDebridFailureReason
    {
        // Not established.
        Unknown,

        // No Real-Debrid key is configured at all.
        NotConfigured,

        // HTTP 401/403: the key or account was refused.
        Authentication,

        // HTTP 429.
        RateLimited,

        // The HTTP request timed out.
        Timeout,

        // The request could not be sent or the connection failed.
        Network,

        // HTTP 404, or Real-Debrid reported the torrent itself as errored.
        ResourceUnavailable,

        // The status polling budget ran out: every request answered but
        // the torrent never reached the awaited status in time.
        NotReadyInTime,

        // The request succeeded but carried no playable link.
        NoPlayableResult,

        // HTTP 500/502/503/504.
        ServiceFailure,

        // The request succeeded but the reply was empty/unreadable.
        UnexpectedResponse,

        // Another non-success HTTP status (e.g. 400).
        Rejected
    }

    // Thrown by RealDebridService for every failure it can classify.
    // Derives from InvalidOperationException, which several of these
    // sites already threw, so existing catch behaviour is unchanged.
    // Message is always a fixed sentence (optionally with a numeric HTTP
    // status): never a URL, token, torrent identifier or response body.
    public sealed class RealDebridException : InvalidOperationException
    {
        public RealDebridException(RealDebridFailureReason reason, string message)
            : this(reason, message, null)
        {
        }

        public RealDebridException(RealDebridFailureReason reason, string message, int? httpStatus)
            : base(message)
        {
            Reason = reason;
            HttpStatus = httpStatus;
        }

        public RealDebridFailureReason Reason { get; private set; }

        public int? HttpStatus { get; private set; }
    }

    // =============================================================
    // ADD MAGNET RESPONSE
    // =============================================================

    [DataContract]
    public class RealDebridAddMagnetResponse
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "uri")]
        public string Uri { get; set; }
    }

    // =============================================================
    // TORRENT INFO
    // =============================================================

    [DataContract]
    public class RealDebridTorrentInfo
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "links")]
        public List<string> Links { get; set; }
    }

    // =============================================================
    // UNRESTRICT LINK RESPONSE
    // =============================================================

    [DataContract]
    public class RealDebridUnrestrictResponse
    {
        [DataMember(Name = "download")]
        public string Download { get; set; }

        [DataMember(Name = "filename")]
        public string Filename { get; set; }
    }
}
