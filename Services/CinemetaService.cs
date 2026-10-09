using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // CINEMETA SERVICE
    // =============================================================
    //
    // Cinemeta-First Addon Settings Architecture slice.
    //
    // HTTP client for Cinemeta's "catalog" and "meta" resources - the two
    // resources StremioAddonService intentionally does NOT implement
    // (see its own class remarks: it is a stream/manifest-only client by
    // design). This is a separate, parallel client rather than an
    // extension of StremioAddonService, because Cinemeta is
    // architecturally a metadata/catalogue addon, not a stream addon -
    // exactly the distinction this slice's capability model (see
    // AddonCapabilities in Models/AddonInstance.cs) exists to represent.
    //
    // Talks to the fixed, official Cinemeta base URL directly (not the
    // user-configurable AddonInstance.ManifestUrl for the seeded
    // "cinemeta" instance, though the two are the same host) - Cinemeta
    // needs no configuration and is not something a user "installs" with
    // a personalised URL the way Torrentio/Comet are, so there is
    // nothing to look up per-request.
    //
    // Every endpoint here was live-verified during this slice's own
    // architecture audit (claude/Cinemeta_Capability_Audit.md and this
    // slice's own report) - shapes are not guessed.
    public class CinemetaService
    {
        private const string BaseUrl = "https://v3-cinemeta.strem.io";

        // =========================================================
        // GET MANIFEST
        // =========================================================
        //
        // Slice 13 (Cinemeta-First Provider & Addon Architecture): added
        // for protocol parity with the catalog/meta resources below -
        // nothing in this codebase requires a live manifest fetch to
        // operate (CoreAddonSeedingService already seeds Cinemeta's
        // identity from a prior verified fetch, and the three catalog
        // ids this service is called with are already documented/stable -
        // see claude/Cinemeta_Capability_Audit.md), so this is not wired
        // into a caller yet. Same fail-soft contract as GetCatalogAsync/
        // GetMetaAsync: never throws, null on failure.
        public async Task<CinemetaManifest> GetManifestAsync()
        {
            string url = BaseUrl + "/manifest.json";

            try
            {
                string json = await DownloadStringAsync(url);

                return Deserialize<CinemetaManifest>(json);
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // GET CATALOG
        // =========================================================
        //
        // type: "movie" or "series". catalogId: "top" ("Popular"),
        // "year" (requires extraSegment "genre=<year>"), "imdbRating"
        // ("Featured"). extraSegment is an already-formed Stremio "extra"
        // path segment (e.g. "genre=2024" or "search=batman"), or null
        // for none. Never throws for an empty/malformed result - callers
        // get an empty list, consistent with StremioAddonService's own
        // "never take down the caller for one addon's bad response"
        // policy.
        public async Task<List<CinemetaCatalogItem>> GetCatalogAsync(
            string type,
            string catalogId,
            string extraSegment = null)
        {
            if (string.IsNullOrWhiteSpace(type) ||
                string.IsNullOrWhiteSpace(catalogId))
            {
                return new List<CinemetaCatalogItem>();
            }

            try
            {
                return await GetCatalogOrThrowAsync(type, catalogId, extraSegment);
            }
            catch
            {
                // Unreachable/malformed - honest empty result, same
                // policy as CatalogueService's own per-title try/catch.
                return new List<CinemetaCatalogItem>();
            }
        }

        // =========================================================
        // SEARCH CATALOG (Slice 19A - failure is NOT an empty result)
        // =========================================================
        //
        // Same request GetCatalogAsync("<type>", "top", "search=<query>")
        // would make, but a network/HTTP/parse failure is thrown to the
        // caller instead of being converted into an empty list, so Search
        // can tell "the provider answered with no matches" apart from "the
        // request failed". A successful response with no matches returns
        // an empty list. Used only by SearchView; every other caller keeps
        // the fail-soft GetCatalogAsync above.
        public Task<List<CinemetaCatalogItem>> SearchCatalogAsync(string type, string query)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("A catalog type is required.", nameof(type));
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("A search query is required.", nameof(query));
            }

            return GetCatalogOrThrowAsync(
                type,
                "top",
                "search=" + Uri.EscapeDataString(query.Trim()));
        }

        // Shared by GetCatalogAsync (which swallows) and SearchCatalogAsync
        // (which does not). Throws on a transport failure, or when the
        // response body is empty/unparseable. A well-formed response with
        // no "metas" entries is a legitimate empty result.
        private async Task<List<CinemetaCatalogItem>> GetCatalogOrThrowAsync(
            string type,
            string catalogId,
            string extraSegment)
        {
            string url =
                BaseUrl +
                "/catalog/" +
                Uri.EscapeDataString(type) +
                "/" +
                Uri.EscapeDataString(catalogId) +
                (string.IsNullOrWhiteSpace(extraSegment)
                    ? string.Empty
                    : "/" + extraSegment) +
                ".json";

            string json = await DownloadStringAsync(url);

            CinemetaCatalogResponse response =
                Deserialize<CinemetaCatalogResponse>(json);

            if (response == null)
            {
                throw new InvalidDataException("Cinemeta returned an empty catalog response.");
            }

            return response.Metas ?? new List<CinemetaCatalogItem>();
        }

        // =========================================================
        // GET META
        // =========================================================
        //
        // type: "movie" or "series". imdbId: e.g. "tt0111161". Returns
        // null (never throws) if the title isn't found or the addon is
        // unreachable - callers must treat null as "no Cinemeta data for
        // this id," never as an error to surface.
        public async Task<CinemetaMeta> GetMetaAsync(string type, string imdbId)
        {
            if (string.IsNullOrWhiteSpace(type) ||
                string.IsNullOrWhiteSpace(imdbId))
            {
                return null;
            }

            string url =
                BaseUrl +
                "/meta/" +
                Uri.EscapeDataString(type) +
                "/" +
                Uri.EscapeDataString(imdbId) +
                ".json";

            try
            {
                string json = await DownloadStringAsync(url);

                CinemetaMetaEnvelope envelope =
                    Deserialize<CinemetaMetaEnvelope>(json);

                return envelope?.Meta;
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // HTTP DOWNLOAD
        // =========================================================
        //
        // Same pattern as StremioAddonService.DownloadStringAsync -
        // WebClient over TLS 1.2, deliberately not shared/refactored
        // into a common base class per this project's convention of not
        // introducing shared infrastructure for a same-slice need
        // (mirrors CatalogueService's own BuildImageUrl duplication
        // rationale).
        private async Task<string> DownloadStringAsync(string url)
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

            var serializer = new DataContractJsonSerializer(typeof(T));

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }

    // =============================================================
    // CINEMETA MANIFEST
    // =============================================================
    //
    // Field set matches the manifest shape already recorded for the
    // "cinemeta" core addon definition in CoreAddonSeedingService
    // (resources: catalog/meta/addon_catalog, types: movie/series,
    // idPrefixes: tt) - reproduced here as a real response model rather
    // than the AddonInstance's own static fields, for any future caller
    // that needs a live manifest fetch instead of the seeded snapshot.
    [DataContract]
    public class CinemetaManifest
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "version")]
        public string Version { get; set; }

        [DataMember(Name = "resources")]
        public List<string> Resources { get; set; }

        [DataMember(Name = "types")]
        public List<string> Types { get; set; }

        [DataMember(Name = "idPrefixes")]
        public List<string> IdPrefixes { get; set; }

        [DataMember(Name = "catalogs")]
        public List<CinemetaManifestCatalog> Catalogs { get; set; }
    }

    // One entry of the manifest's "catalogs" array - e.g.
    // { type: "movie", id: "top", name: "Popular" }.
    [DataContract]
    public class CinemetaManifestCatalog
    {
        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    // =============================================================
    // CINEMETA CATALOG RESPONSE
    // =============================================================

    [DataContract]
    public class CinemetaCatalogResponse
    {
        [DataMember(Name = "metas")]
        public List<CinemetaCatalogItem> Metas { get; set; }
    }

    // =============================================================
    // CINEMETA CATALOG ITEM (lightweight preview shape)
    // =============================================================
    //
    // Field set live-confirmed against GET /catalog/movie/top.json -
    // catalog list items are a lighter preview shape than full /meta/
    // objects (no background/logo/videos/trailers).

    [DataContract]
    public class CinemetaCatalogItem
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "imdb_id")]
        public string ImdbId { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "poster")]
        public string Poster { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "genres")]
        public List<string> Genres { get; set; }

        [DataMember(Name = "imdbRating")]
        public string ImdbRating { get; set; }

        [DataMember(Name = "releaseInfo")]
        public string ReleaseInfo { get; set; }

        [DataMember(Name = "year")]
        public string Year { get; set; }

        [DataMember(Name = "released")]
        public string Released { get; set; }
    }

    // =============================================================
    // CINEMETA META ENVELOPE
    // =============================================================

    [DataContract]
    public class CinemetaMetaEnvelope
    {
        [DataMember(Name = "meta")]
        public CinemetaMeta Meta { get; set; }
    }

    // =============================================================
    // CINEMETA META (full movie/series object)
    // =============================================================
    //
    // Field set live-confirmed against GET /meta/movie/tt0111161.json
    // and GET /meta/series/tt0903747.json.

    [DataContract]
    public class CinemetaMeta
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "imdb_id")]
        public string ImdbId { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "year")]
        public string Year { get; set; }

        [DataMember(Name = "releaseInfo")]
        public string ReleaseInfo { get; set; }

        [DataMember(Name = "released")]
        public string Released { get; set; }

        [DataMember(Name = "runtime")]
        public string Runtime { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "genres")]
        public List<string> Genres { get; set; }

        [DataMember(Name = "imdbRating")]
        public string ImdbRating { get; set; }

        [DataMember(Name = "cast")]
        public List<string> Cast { get; set; }

        [DataMember(Name = "director")]
        public List<string> Director { get; set; }

        [DataMember(Name = "writer")]
        public List<string> Writer { get; set; }

        [DataMember(Name = "country")]
        public string Country { get; set; }

        [DataMember(Name = "poster")]
        public string Poster { get; set; }

        [DataMember(Name = "background")]
        public string Background { get; set; }

        [DataMember(Name = "logo")]
        public string Logo { get; set; }

        // Movies only - empty/absent for series. Real YouTube trailer
        // ids, live-confirmed (see this slice's audit correction of the
        // earlier "no trailer field" finding).
        [DataMember(Name = "trailerStreams")]
        public List<CinemetaTrailerStream> TrailerStreams { get; set; }

        // Series only - episodes (and specials, season 0). Absent/empty
        // for movies.
        [DataMember(Name = "videos")]
        public List<CinemetaVideo> Videos { get; set; }
    }

    [DataContract]
    public class CinemetaTrailerStream
    {
        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "ytId")]
        public string YouTubeId { get; set; }
    }

    // =============================================================
    // CINEMETA VIDEO (one series episode)
    // =============================================================

    [DataContract]
    public class CinemetaVideo
    {
        // Already in "<imdbId>:<season>:<episode>" shape - the same
        // shape ContentRequest.BuildStremioId() produces itself.
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "season")]
        public int Season { get; set; }

        [DataMember(Name = "number")]
        public int Number { get; set; }

        [DataMember(Name = "overview")]
        public string Overview { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "firstAired")]
        public string FirstAired { get; set; }

        [DataMember(Name = "released")]
        public string Released { get; set; }

        [DataMember(Name = "rating")]
        public string Rating { get; set; }

        [DataMember(Name = "thumbnail")]
        public string Thumbnail { get; set; }
    }
}
