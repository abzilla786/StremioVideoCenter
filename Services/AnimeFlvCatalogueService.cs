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
    // ANIMEFLV CATALOGUE SERVICE (Slice 15)
    // =============================================================
    //
    // HTTP client for the AnimeFLV/AnimES addon's OWN "catalog" resource
    // (com.animeflv.stremio.addon, public instance at
    // https://pigamer37.alwaysdata.net - the same instance
    // CoreAddonSeedingService already seeds as a core addon for STREAM
    // discovery). Separate, parallel client from CinemetaService, exactly
    // like CinemetaService is itself separate from StremioAddonService -
    // this addon's catalog capability is architecturally distinct from
    // both Cinemeta's metadata catalogue and this addon's own stream
    // resolution (already used via StremioAddonRegistry).
    //
    // =============================================================
    // LIVE VERIFICATION PERFORMED THIS SLICE (not invented)
    // =============================================================
    //
    // Slice 13/14A's own audits explicitly left AnimeFLV's real catalog
    // ids/shape unverified ("do not invent AnimeFLV catalog ids"). This
    // slice fetched the addon's live manifest.json directly and found:
    //
    //   - 13 real catalogs, none of which is a plain/unfiltered browse.
    //     Every single one requires either a "search" extra (isRequired:
    //     true) or a "genre" extra (isRequired: true) - there is no
    //     Cinemeta-style "top"/"imdbRating" catalog with only optional
    //     extras. This is why AnimeFLV cannot honestly back a ranked
    //     "Featured"/"Popular" Home row (see CatalogueService remarks) -
    //     every request must already be a search or a genre choice.
    //   - Six per-source search catalogs: type/id pairs
    //     ("AnimeFLV","animeflv"), ("AnimeAV1","animeav1"),
    //     ("Henaojara","henaojara"), ("TioAnime","tioanime"),
    //     ("AnimeJara","animejara"), ("JKAnime","jkanime") - each with a
    //     required "search" extra. This class only queries the primary
    //     "AnimeFLV" source (see GetSearchCatalogAsync remarks) -
    //     querying all six for every global search keystroke would fan
    //     out 6x against a slow, real-time-scraping backend that already
    //     timed out on single requests during this slice's own testing.
    //   - Six matching genre-browse catalogs, id "<source>|genres", each
    //     with a required "genre" extra. GetGenreCatalogAsync below uses
    //     the same primary "AnimeFLV" source. The "genre" extra's real,
    //     live "options" list (39 verified values - accion, artes-
    //     marciales, aventura, ... yaoi, yuri) is reproduced in
    //     GenreOptions below - not invented.
    //   - The catalog "type" value is the source's own display name
    //     (e.g. "AnimeFLV"), NOT "anime"/"series"/"movie" - unusual
    //     versus Cinemeta, but exactly what this addon's own manifest
    //     declares, and the value this class sends on every request.
    //   - idPrefixes are native per-source slugs (e.g. "animeflv:...")
    //     PLUS "tt" passthrough - items from these catalogs are NOT
    //     guaranteed to carry an IMDb id. Their native id is used as-is
    //     for MediaItem.StremioId, exactly like ContentRequest's existing
    //     ExplicitStremioId escape hatch already supports for a non-"tt"
    //     id (see PlaySelectedMediaAsync, unchanged this slice) - no new
    //     playback plumbing was required.
    //
    // What could NOT be independently verified this slice: every attempt
    // to fetch an actual catalog RESPONSE body (genre browse or search)
    // timed out (the addon appears to scrape its underlying anime sites
    // live, rather than serving a pre-built index - consistent with why
    // earlier audits never captured this either). The item-shape parsing
    // below therefore follows the standard Stremio "meta preview object"
    // fields (id/type/name/poster/description/releaseInfo/imdbRating) -
    // the shape any addon MUST return for its declared "catalog" resource
    // to function in real Stremio at all - rather than a fabricated,
    // addon-specific shape. Every field is optional/nullable and parsing
    // never throws, exactly like CinemetaCatalogItem.
    public class AnimeFlvCatalogueService
    {
        private const string BaseUrl = "https://pigamer37.alwaysdata.net";

        // The addon's own catalog "type" value for its primary source -
        // see class remarks. Sent verbatim; not "anime"/"series".
        private const string PrimarySourceType = "AnimeFLV";
        private const string PrimarySourceSearchCatalogId = "animeflv";
        private const string PrimarySourceGenreCatalogId = "animeflv|genres";

        // Live-verified genre options for the "animeflv|genres" catalog's
        // required "genre" extra (see class remarks) - the ONLY genre
        // values this addon actually accepts for its primary source.
        public static readonly IReadOnlyList<string> GenreOptions = new List<string>
        {
            "accion", "artes-marciales", "aventura", "carreras", "ciencia-ficcion",
            "comedia", "demencia", "demonios", "deportes", "drama", "ecchi",
            "escolares", "espacial", "fantasia", "harem", "historico", "infantil",
            "josei", "juegos", "magia", "mecha", "militar", "misterio", "musica",
            "parodia", "policia", "psicologico", "recuentos-de-la-vida", "romance",
            "samurai", "seinen", "shoujo", "shounen", "sobrenatural", "superpoderes",
            "suspenso", "terror", "vampiros", "yaoi", "yuri"
        };

        // =========================================================
        // GENRE BROWSE
        // =========================================================
        //
        // genre must be one of GenreOptions (verbatim, e.g. "accion") -
        // callers (AnimeBrowseView) only ever offer these exact values,
        // never free text, matching "a filter must only be enabled when
        // the active provider can actually support it."
        public async Task<List<AnimeFlvCatalogItem>> GetGenreCatalogAsync(string genre, int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                return new List<AnimeFlvCatalogItem>();
            }

            string extra = "genre=" + Uri.EscapeDataString(genre) + (skip > 0 ? "&skip=" + skip : string.Empty);

            return await GetCatalogAsync(PrimarySourceGenreCatalogId, extra);
        }

        // =========================================================
        // SEARCH
        // =========================================================
        //
        // Primary "AnimeFLV" source only - see class remarks for why the
        // other five per-source catalogs are deliberately not queried.
        public async Task<List<AnimeFlvCatalogItem>> GetSearchCatalogAsync(string query, int skip = 0)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<AnimeFlvCatalogItem>();
            }

            string extra = "search=" + Uri.EscapeDataString(query) + (skip > 0 ? "&skip=" + skip : string.Empty);

            return await GetCatalogAsync(PrimarySourceSearchCatalogId, extra);
        }

        // =========================================================
        // SHARED CATALOG FETCH
        // =========================================================
        //
        // Never throws - an unreachable or slow (this addon is a live
        // scraper - see class remarks) response yields an honest empty
        // list, exactly like CinemetaService's own fail-soft contract.
        // No explicit WebClient timeout override, same as every other
        // HTTP client in this codebase (WebClient's ~100s default) -
        // deliberately generous given this addon's observed latency,
        // rather than failing fast on a source that may still succeed.
        private async Task<List<AnimeFlvCatalogItem>> GetCatalogAsync(string catalogId, string extraSegment)
        {
            string url =
                BaseUrl +
                "/catalog/" +
                Uri.EscapeDataString(PrimarySourceType) +
                "/" +
                Uri.EscapeDataString(catalogId) +
                "/" +
                extraSegment +
                ".json";

            try
            {
                string json = await DownloadStringAsync(url);

                AnimeFlvCatalogResponse response = Deserialize<AnimeFlvCatalogResponse>(json);

                return response?.Metas ?? new List<AnimeFlvCatalogItem>();
            }
            catch
            {
                return new List<AnimeFlvCatalogItem>();
            }
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            return await Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "StremioVideoCenter/1.0";

                    return client.DownloadString(url);
                }
            });
        }

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
    // ANIMEFLV CATALOG RESPONSE / ITEM
    // =============================================================
    //
    // Standard Stremio "meta preview object" shape (see class remarks) -
    // every field optional/nullable, not independently field-verified
    // against a live response this slice (every attempt timed out), but
    // this is the shape the Stremio protocol itself requires a
    // catalog-resource addon to return.

    [DataContract]
    public class AnimeFlvCatalogResponse
    {
        [DataMember(Name = "metas")]
        public List<AnimeFlvCatalogItem> Metas { get; set; }
    }

    [DataContract]
    public class AnimeFlvCatalogItem
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "poster")]
        public string Poster { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "releaseInfo")]
        public string ReleaseInfo { get; set; }

        [DataMember(Name = "imdbRating")]
        public string ImdbRating { get; set; }

        [DataMember(Name = "genres")]
        public List<string> Genres { get; set; }
    }
}
