using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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

            StremioAddonManifest manifest =
                Deserialize<StremioAddonManifest>(json);

            // "resources" is parsed separately below because it can mix
            // plain strings and objects in the same array (see
            // ParseResources for details) - a shape
            // DataContractJsonSerializer cannot handle for a single
            // List<T> property.
            if (manifest != null)
            {
                manifest.Resources = ParseResources(json);
            }

            return manifest;
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

        // =========================================================
        // PARSE RESOURCES (manifest "resources" field)
        // =========================================================
        //
        // Per the Stremio addon protocol, each entry of the manifest's
        // "resources" array may be either a bare string (e.g. "stream")
        // or an object carrying resource-specific overrides (e.g.
        // {"name":"stream","types":["movie","series"],"idPrefixes":["tt"]}).
        // Real-world manifests (confirmed live against Comet and
        // MediaFusion) mix both shapes in the same array.
        // DataContractJsonSerializer requires one static shape per
        // array and cannot deserialize that directly, so this field is
        // intentionally left off the [DataContract] on
        // StremioAddonManifest and populated here instead, using
        // Newtonsoft.Json (already a project dependency - see
        // MpvIpcClient.cs) for a small, targeted second pass over the
        // same raw manifest JSON.
        private List<StremioManifestResource> ParseResources(string json)
        {
            var result = new List<StremioManifestResource>();

            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            JObject root;

            try
            {
                root = JObject.Parse(json);
            }
            catch
            {
                // The primary DataContractJsonSerializer pass in
                // GetManifestAsync already succeeded by the time this
                // runs, so a failure here means something unexpected
                // about this specific field - fail soft rather than
                // taking down manifest parsing entirely.
                return result;
            }

            JToken resourcesToken = root["resources"];

            if (resourcesToken == null ||
                resourcesToken.Type != JTokenType.Array)
            {
                return result;
            }

            foreach (JToken entry in (JArray)resourcesToken)
            {
                if (entry.Type == JTokenType.String)
                {
                    result.Add(new StremioManifestResource
                    {
                        Name = entry.Value<string>()
                    });
                }
                else if (entry.Type == JTokenType.Object)
                {
                    result.Add(new StremioManifestResource
                    {
                        Name = entry.Value<string>("name"),

                        Types = entry["types"] != null
                            ? entry["types"].ToObject<List<string>>()
                            : null,

                        IdPrefixes = entry["idPrefixes"] != null
                            ? entry["idPrefixes"].ToObject<List<string>>()
                            : null
                    });
                }

                // Any other shape (number, null, nested array) is not
                // part of the Stremio protocol - skip it rather than
                // throwing.
            }

            return result;
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

        [DataMember(Name = "logo")]
        public string Logo { get; set; }

        // Not a [DataMember] - populated separately by
        // StremioAddonService.ParseResources(). See that method for why
        // DataContractJsonSerializer cannot be used for this field.
        public List<StremioManifestResource> Resources { get; set; }

        [DataMember(Name = "types")]
        public List<string> Types { get; set; }

        [DataMember(Name = "idPrefixes")]
        public List<string> IdPrefixes { get; set; }

        [DataMember(Name = "behaviorHints")]
        public StremioManifestBehaviorHints BehaviorHints { get; set; }
    }

    // =============================================================
    // STREMIO MANIFEST RESOURCE
    // =============================================================
    //
    // One entry of a manifest's "resources" array. See
    // StremioAddonService.ParseResources() for why this is populated
    // via a separate Newtonsoft.Json pass rather than as a
    // [DataMember] on StremioAddonManifest.
    public class StremioManifestResource
    {
        public string Name { get; set; }
        public List<string> Types { get; set; }
        public List<string> IdPrefixes { get; set; }
    }

    // =============================================================
    // STREMIO MANIFEST BEHAVIOR HINTS
    // =============================================================

    [DataContract]
    public class StremioManifestBehaviorHints
    {
        [DataMember(Name = "configurable")]
        public bool? Configurable { get; set; }

        [DataMember(Name = "configurationRequired")]
        public bool? ConfigurationRequired { get; set; }
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