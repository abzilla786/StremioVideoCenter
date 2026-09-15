using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Talks to the Real-Debrid REST API only. Knows nothing about Stremio,
    // MPV, or the player - StreamResolverService is the only thing that
    // calls this, and only when a stream has no direct Url of its own.
    //
    // Deliberately mirrors TmdbService's HTTP/JSON approach (HttpClient +
    // DataContractJsonSerializer, Bearer auth, 15s-ish timeouts) rather than
    // introducing Newtonsoft into this layer - Newtonsoft is already used by
    // the player's MpvIpcClient, but the metadata/addon layer (TmdbService,
    // StremioAddonService) has consistently used DataContractJsonSerializer,
    // and this keeps that layer internally consistent instead of mixing two
    // JSON frameworks in the same architectural tier.
    public class RealDebridService
    {
        private const string BaseUrl = "https://api.real-debrid.com/rest/1.0/";

        private readonly string accessToken;

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
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException(
                    "Real-Debrid API token has not been configured.");
            }

            if (string.IsNullOrWhiteSpace(infoHash))
            {
                throw new ArgumentException("infoHash cannot be empty.", nameof(infoHash));
            }

            string magnet = "magnet:?xt=urn:btih:" + infoHash;

            RealDebridAddMagnetResponse added = await PostFormAsync<RealDebridAddMagnetResponse>(
                "torrents/addMagnet",
                new Dictionary<string, string> { { "magnet", magnet } });

            if (added == null || string.IsNullOrWhiteSpace(added.Id))
            {
                throw new InvalidOperationException(
                    "Real-Debrid did not return a torrent id for this magnet.");
            }

            await WaitForStatusAsync(
                added.Id,
                maxAttempts: 15,
                delayMilliseconds: 1000,
                acceptableStatuses: new[] { "waiting_files_selection", "downloaded" });

            string fileSelection = fileIdx.HasValue
                ? (fileIdx.Value + 1).ToString()
                : "all";

            await PostFormAsync<object>(
                "torrents/selectFiles/" + added.Id,
                new Dictionary<string, string> { { "files", fileSelection } });

            RealDebridTorrentInfo info = await WaitForStatusAsync(
                added.Id,
                maxAttempts: 30,
                delayMilliseconds: 2000,
                acceptableStatuses: new[] { "downloaded" });

            if (info.Links == null || info.Links.Count == 0)
            {
                throw new InvalidOperationException(
                    "Real-Debrid did not return a downloadable link for this torrent.");
            }

            string hostLink = info.Links[0];

            RealDebridUnrestrictResponse unrestricted = await PostFormAsync<RealDebridUnrestrictResponse>(
                "unrestrict/link",
                new Dictionary<string, string> { { "link", hostLink } });

            if (unrestricted == null || string.IsNullOrWhiteSpace(unrestricted.Download))
            {
                throw new InvalidOperationException(
                    "Real-Debrid was unable to unrestrict this link.");
            }

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
            string[] acceptableStatuses)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                RealDebridTorrentInfo info = await GetTorrentInfoAsync(torrentId);

                if (info == null)
                {
                    throw new InvalidOperationException("Real-Debrid returned no torrent info.");
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
                    throw new InvalidOperationException(
                        "Real-Debrid reported an error resolving this torrent: " + info.Status);
                }

                await Task.Delay(delayMilliseconds);
            }

            throw new TimeoutException(
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
                throw new InvalidOperationException(
                    "Real-Debrid API token has not been configured.");
            }

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
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
            }
        }

        // =========================================================
        // HTTP POST (form-encoded, Bearer auth)
        // =========================================================

        private async Task<T> PostFormAsync<T>(string endpoint, Dictionary<string, string> form)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException(
                    "Real-Debrid API token has not been configured.");
            }

            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri(BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken.Trim());

                using (var content = new FormUrlEncodedContent(form))
                using (var response = await client.PostAsync(endpoint, content))
                {
                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    return Deserialize<T>(json);
                }
            }
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

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
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
