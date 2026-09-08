using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using StremioVideoCenter.Models;

namespace StremioVideoCenter.Services
{
    public class StremioAddonService
    {
        private readonly string _addonUrl;

        public StremioAddonService(string addonUrl)
        {
            if (string.IsNullOrWhiteSpace(addonUrl))
            {
                throw new ArgumentException(
                    "Addon URL cannot be empty.",
                    nameof(addonUrl));
            }

            _addonUrl = addonUrl.TrimEnd('/');
        }

        // =========================================================
        // GET MANIFEST
        // =========================================================

        public async Task<StremioAddonManifest> GetManifestAsync()
        {
            string url =
                _addonUrl + "/manifest.json";

            string json =
                await DownloadStringAsync(url);

            return Deserialize<StremioAddonManifest>(json);
        }

        // =========================================================
        // GET STREAMS
        // =========================================================

        public async Task<List<StremioStream>> GetStreamsAsync(
            string type,
            string id)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return new List<StremioStream>();
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return new List<StremioStream>();
            }

            string url =
                _addonUrl +
                "/stream/" +
                Uri.EscapeDataString(type) +
                "/" +
                Uri.EscapeDataString(id) +
                ".json";

            string json =
                await DownloadStringAsync(url);

            StremioStreamResponse response =
                Deserialize<StremioStreamResponse>(json);

            if (response == null ||
                response.Streams == null)
            {
                return new List<StremioStream>();
            }

            return response.Streams;
        }

        // =========================================================
        // GET FIRST DIRECT PLAYABLE URL
        // =========================================================

        public async Task<string> GetFirstPlayableUrlAsync(
            string type,
            string id)
        {
            List<StremioStream> streams =
                await GetStreamsAsync(type, id);

            if (streams == null ||
                streams.Count == 0)
            {
                return null;
            }

            foreach (StremioStream stream in streams)
            {
                if (stream == null)
                {
                    continue;
                }

                // For this first playback milestone we only
                // support direct HTTP/HTTPS stream URLs.
                if (!string.IsNullOrWhiteSpace(stream.Url))
                {
                    return stream.Url;
                }
            }

            return null;
        }

        // =========================================================
        // TEST CONNECTION
        // =========================================================

        public async Task<List<StremioStream>> TestConnectionAsync()
        {
            return await GetStreamsAsync(
                "movie",
                "tt1254207");
        }

        // =========================================================
        // HTTP DOWNLOAD
        // =========================================================

        private async Task<string> DownloadStringAsync(
            string url)
        {
            return await Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12;

                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] =
                        "StremioVideoCenter/1.0";

                    return client.DownloadString(url);
                }
            });
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

            var serializer =
                new DataContractJsonSerializer(
                    typeof(T));

            using (var stream =
                   new MemoryStream(
                       Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }

    // =============================================================
    // STREMIO MANIFEST
    // =============================================================

    [DataContract]
    public class StremioAddonManifest
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "resources")]
        public List<string> Resources { get; set; }

        [DataMember(Name = "types")]
        public List<string> Types { get; set; }

        [DataMember(Name = "idPrefixes")]
        public List<string> IdPrefixes { get; set; }
    }

    // =============================================================
    // STREMIO STREAM RESPONSE
    // =============================================================

    [DataContract]
    public class StremioStreamResponse
    {
        [DataMember(Name = "streams")]
        public List<StremioStream> Streams { get; set; }
    }
}