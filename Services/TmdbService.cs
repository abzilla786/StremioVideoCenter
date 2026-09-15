using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    public class TmdbService
    {
        // =========================================================
        // TMDB API
        // =========================================================

        private const string BaseUrl =
            "https://api.themoviedb.org/3/";

        private readonly string accessToken;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public TmdbService(string accessToken)
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
                string json =
                    await GetAsync("movie/550");

                return !string.IsNullOrWhiteSpace(json);
            }
            catch
            {
                return false;
            }
        }

        // =========================================================
        // MOVIE DISCOVERY
        // =========================================================

        public async Task<List<TmdbDiscoveryItem>>
            GetMovieCandidatesAsync(
                int page = 1)
        {
            string endpoint =
                "discover/movie" +
                "?include_adult=false" +
                "&include_video=false" +
                "&language=en-US" +
                "&page=" +
                page +
                "&sort_by=popularity.desc" +
                "&vote_count.gte=50";

            TmdbDiscoveryResponse response =
                await GetDiscoveryResponseAsync(
                    endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // TV DISCOVERY
        // =========================================================

        public async Task<List<TmdbDiscoveryItem>>
            GetTvCandidatesAsync(
                int page = 1)
        {
            string endpoint =
                "discover/tv" +
                "?include_adult=false" +
                "&language=en-US" +
                "&page=" +
                page +
                "&sort_by=popularity.desc" +
                "&vote_count.gte=50";

            TmdbDiscoveryResponse response =
                await GetDiscoveryResponseAsync(
                    endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // ANIME DISCOVERY
        // =========================================================

        public async Task<List<TmdbDiscoveryItem>>
            GetAnimeCandidatesAsync(
                int page = 1)
        {
            string endpoint =
                "discover/tv" +
                "?include_adult=false" +
                "&language=en-US" +
                "&page=" +
                page +
                "&sort_by=popularity.desc" +
                "&vote_count.gte=20" +
                "&with_genres=16" +
                "&with_origin_country=JP";

            TmdbDiscoveryResponse response =
                await GetDiscoveryResponseAsync(
                    endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // MOVIE SEARCH
        // =========================================================
        //
        // Returns the same TmdbDiscoveryItem shape as the discovery
        // endpoints above - TMDB's search/movie response is identical in
        // structure to discover/movie, so no separate model is needed.

        public async Task<List<TmdbDiscoveryItem>>
            SearchMoviesAsync(
                string query,
                int page = 1)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<TmdbDiscoveryItem>();
            }

            string endpoint =
                "search/movie" +
                "?include_adult=false" +
                "&language=en-US" +
                "&page=" +
                page +
                "&query=" +
                Uri.EscapeDataString(query);

            TmdbDiscoveryResponse response =
                await GetDiscoveryResponseAsync(
                    endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // TV SEARCH
        // =========================================================

        public async Task<List<TmdbDiscoveryItem>>
            SearchTvAsync(
                string query,
                int page = 1)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<TmdbDiscoveryItem>();
            }

            string endpoint =
                "search/tv" +
                "?include_adult=false" +
                "&language=en-US" +
                "&page=" +
                page +
                "&query=" +
                Uri.EscapeDataString(query);

            TmdbDiscoveryResponse response =
                await GetDiscoveryResponseAsync(
                    endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // MOVIE DETAILS + VIDEOS + EXTERNAL IDS
        // =========================================================
        //
        // append_to_response now also requests external_ids, which is
        // where TMDB exposes the IMDb id (imdb_id) - the identifier Stremio
        // addons actually key streams on. This is additive: existing videos
        // parsing (GetBestTrailerOrTeaserAsync, etc.) is unaffected.

        public async Task<TmdbDetailsItem>
            GetMovieDetailsAsync(
                int movieId)
        {
            string endpoint =
                "movie/" +
                movieId +
                "?language=en-US" +
                "&append_to_response=videos,external_ids";

            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbDetailsItem>(
                json);
        }

        // =========================================================
        // TV DETAILS + VIDEOS + EXTERNAL IDS
        // =========================================================

        public async Task<TmdbDetailsItem>
            GetTvDetailsAsync(
                int tvId)
        {
            string endpoint =
                "tv/" +
                tvId +
                "?language=en-US" +
                "&append_to_response=videos,external_ids";

            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbDetailsItem>(
                json);
        }

        // =========================================================
        // TV SEASON DETAILS
        // =========================================================
        //
        // Needed to support Stremio's per-episode stream requests, which
        // are keyed as "<imdbId>:<season>:<episode>" rather than by a
        // TV-show-level id alone.

        public async Task<TmdbSeasonDetails>
            GetTvSeasonAsync(
                int tvId,
                int seasonNumber)
        {
            string endpoint =
                "tv/" +
                tvId +
                "/season/" +
                seasonNumber +
                "?language=en-US";

            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbSeasonDetails>(
                json);
        }

        // =========================================================
        // TV EPISODE DETAILS
        // =========================================================

        public async Task<TmdbEpisodeDetails>
            GetTvEpisodeAsync(
                int tvId,
                int seasonNumber,
                int episodeNumber)
        {
            string endpoint =
                "tv/" +
                tvId +
                "/season/" +
                seasonNumber +
                "/episode/" +
                episodeNumber +
                "?language=en-US";

            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbEpisodeDetails>(
                json);
        }

        // =========================================================
        // BEST TRAILER / TEASER
        // =========================================================

        public async Task<TmdbVideoItem>
            GetBestTrailerOrTeaserAsync(
                int id,
                bool isTv)
        {
            TmdbDetailsItem details;

            if (isTv)
            {
                details =
                    await GetTvDetailsAsync(id);
            }
            else
            {
                details =
                    await GetMovieDetailsAsync(id);
            }

            if (details == null ||
                details.Videos == null ||
                details.Videos.Results == null)
            {
                return null;
            }

            var videos =
                details.Videos.Results;

            // =========================================================
            // PREFER OFFICIAL ENGLISH TRAILER
            // =========================================================

            var video =
                FindBestVideo(
                    videos,
                    "Trailer",
                    true,
                    true);

            if (video != null)
            {
                return video;
            }

            // =========================================================
            // PREFER OFFICIAL ENGLISH TEASER
            // =========================================================

            video =
                FindBestVideo(
                    videos,
                    "Teaser",
                    true,
                    true);

            if (video != null)
            {
                return video;
            }

            // =========================================================
            // ANY ENGLISH TRAILER
            // =========================================================

            video =
                FindBestVideo(
                    videos,
                    "Trailer",
                    false,
                    true);

            if (video != null)
            {
                return video;
            }

            // =========================================================
            // ANY ENGLISH TEASER
            // =========================================================

            video =
                FindBestVideo(
                    videos,
                    "Teaser",
                    false,
                    true);

            if (video != null)
            {
                return video;
            }

            // =========================================================
            // FALLBACK TO ANY TRAILER
            // =========================================================

            video =
                FindBestVideo(
                    videos,
                    "Trailer",
                    false,
                    false);

            if (video != null)
            {
                return video;
            }

            // =========================================================
            // FALLBACK TO ANY TEASER
            // =========================================================

            return FindBestVideo(
                videos,
                "Teaser",
                false,
                false);
        }

        // =========================================================
        // FIND BEST VIDEO
        // =========================================================

        private TmdbVideoItem FindBestVideo(
            List<TmdbVideoItem> videos,
            string type,
            bool officialOnly,
            bool englishOnly)
        {
            foreach (var video in videos)
            {
                if (video == null)
                {
                    continue;
                }

                if (!string.Equals(
                        video.Site,
                        "YouTube",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(
                        video.Type,
                        type,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(
                        video.Key))
                {
                    continue;
                }

                if (officialOnly &&
                    !video.Official)
                {
                    continue;
                }

                if (englishOnly &&
                    !string.Equals(
                        video.Iso6391,
                        "en",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return video;
            }

            return null;
        }

        // =========================================================
        // DISCOVERY RESPONSE
        // =========================================================

        private async Task<TmdbDiscoveryResponse>
            GetDiscoveryResponseAsync(
                string endpoint)
        {
            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbDiscoveryResponse>(
                json);
        }

        // =========================================================
        // HTTP REQUEST
        // =========================================================

        private async Task<string> GetAsync(
            string endpoint)
        {
            if (string.IsNullOrWhiteSpace(
                    accessToken))
            {
                throw new InvalidOperationException(
                    "TMDB API credential has not been configured.");
            }

            using (var client =
                new HttpClient())
            {
                client.BaseAddress =
                    new Uri(BaseUrl);

                client.Timeout =
                    TimeSpan.FromSeconds(15);

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        accessToken.Trim());

                client.DefaultRequestHeaders.Accept.Clear();

                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(
                        "application/json"));

                using (var response =
                    await client.GetAsync(endpoint))
                {
                    response.EnsureSuccessStatusCode();

                    return await response.Content
                        .ReadAsStringAsync();
                }
            }
        }

        // =========================================================
        // JSON DESERIALIZATION
        // =========================================================

        private T Deserialize<T>(
            string json)
        {
            var serializer =
                new DataContractJsonSerializer(
                    typeof(T));

            using (var stream =
                new MemoryStream(
                    System.Text.Encoding.UTF8
                        .GetBytes(json)))
            {
                return (T)serializer.ReadObject(
                    stream);
            }
        }
    }

    // =============================================================
    // DISCOVERY RESPONSE
    // =============================================================

    [DataContract]
    public class TmdbDiscoveryResponse
    {
        [DataMember(Name = "page")]
        public int Page { get; set; }

        [DataMember(Name = "total_pages")]
        public int TotalPages { get; set; }

        [DataMember(Name = "total_results")]
        public int TotalResults { get; set; }

        [DataMember(Name = "results")]
        public List<TmdbDiscoveryItem> Results { get; set; }
    }

    // =============================================================
    // DISCOVERY ITEM
    // =============================================================

    [DataContract]
    public class TmdbDiscoveryItem
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "original_title")]
        public string OriginalTitle { get; set; }

        [DataMember(Name = "original_name")]
        public string OriginalName { get; set; }

        [DataMember(Name = "overview")]
        public string Overview { get; set; }

        [DataMember(Name = "poster_path")]
        public string PosterPath { get; set; }

        [DataMember(Name = "backdrop_path")]
        public string BackdropPath { get; set; }

        [DataMember(Name = "release_date")]
        public string ReleaseDate { get; set; }

        [DataMember(Name = "first_air_date")]
        public string FirstAirDate { get; set; }

        [DataMember(Name = "vote_average")]
        public double VoteAverage { get; set; }

        [DataMember(Name = "vote_count")]
        public int VoteCount { get; set; }

        [DataMember(Name = "popularity")]
        public double Popularity { get; set; }

        [DataMember(Name = "original_language")]
        public string OriginalLanguage { get; set; }

        [DataMember(Name = "adult")]
        public bool Adult { get; set; }

        [DataMember(Name = "genre_ids")]
        public List<int> GenreIds { get; set; }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(
                        Title))
                {
                    return Title;
                }

                return Name;
            }
        }

        public string DisplayYear
        {
            get
            {
                string date =
                    !string.IsNullOrWhiteSpace(
                        ReleaseDate)
                        ? ReleaseDate
                        : FirstAirDate;

                if (string.IsNullOrWhiteSpace(
                        date) ||
                    date.Length < 4)
                {
                    return string.Empty;
                }

                return date.Substring(
                    0,
                    4);
            }
        }

        public bool HasPoster
        {
            get
            {
                return
                    !string.IsNullOrWhiteSpace(
                        PosterPath);
            }
        }

        public bool HasBackdrop
        {
            get
            {
                return
                    !string.IsNullOrWhiteSpace(
                        BackdropPath);
            }
        }
    }

    // =============================================================
    // DETAILS ITEM
    // =============================================================
    //
    // Expanded to carry the fields the streaming pipeline and future UI
    // need (poster/backdrop/rating/runtime/genres/seasons, plus external_ids
    // for the IMDb bridge to Stremio). All additions are additive - nothing
    // existing was removed or renamed, so GetBestTrailerOrTeaserAsync and
    // any other existing caller keep working unchanged.

    [DataContract]
    public class TmdbDetailsItem
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "overview")]
        public string Overview { get; set; }

        [DataMember(Name = "poster_path")]
        public string PosterPath { get; set; }

        [DataMember(Name = "backdrop_path")]
        public string BackdropPath { get; set; }

        [DataMember(Name = "vote_average")]
        public double VoteAverage { get; set; }

        [DataMember(Name = "release_date")]
        public string ReleaseDate { get; set; }

        [DataMember(Name = "first_air_date")]
        public string FirstAirDate { get; set; }

        // Movies only.
        [DataMember(Name = "runtime")]
        public int? Runtime { get; set; }

        // TV only - usually a single-element list of typical episode length.
        [DataMember(Name = "episode_run_time")]
        public List<int> EpisodeRunTime { get; set; }

        [DataMember(Name = "genres")]
        public List<TmdbGenre> Genres { get; set; }

        // TV only.
        [DataMember(Name = "seasons")]
        public List<TmdbSeasonSummary> Seasons { get; set; }

        [DataMember(Name = "videos")]
        public TmdbVideoResponse Videos { get; set; }

        [DataMember(Name = "external_ids")]
        public TmdbExternalIds ExternalIds { get; set; }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title))
                {
                    return Title;
                }

                return Name;
            }
        }

        // The identifier Stremio addons actually key streams on.
        // Null when TMDB has no IMDb mapping for this title.
        public string ImdbId
        {
            get
            {
                return ExternalIds?.ImdbId;
            }
        }
    }

    // =============================================================
    // GENRE
    // =============================================================

    [DataContract]
    public class TmdbGenre
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    // =============================================================
    // SEASON SUMMARY (as embedded in TV details)
    // =============================================================

    [DataContract]
    public class TmdbSeasonSummary
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "season_number")]
        public int SeasonNumber { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "episode_count")]
        public int EpisodeCount { get; set; }

        [DataMember(Name = "poster_path")]
        public string PosterPath { get; set; }

        [DataMember(Name = "air_date")]
        public string AirDate { get; set; }
    }

    // =============================================================
    // SEASON DETAILS (full episode list, from /tv/{id}/season/{n})
    // =============================================================

    [DataContract]
    public class TmdbSeasonDetails
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "season_number")]
        public int SeasonNumber { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "overview")]
        public string Overview { get; set; }

        [DataMember(Name = "poster_path")]
        public string PosterPath { get; set; }

        [DataMember(Name = "air_date")]
        public string AirDate { get; set; }

        [DataMember(Name = "episodes")]
        public List<TmdbEpisodeDetails> Episodes { get; set; }
    }

    // =============================================================
    // EPISODE DETAILS
    // =============================================================

    [DataContract]
    public class TmdbEpisodeDetails
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "episode_number")]
        public int EpisodeNumber { get; set; }

        [DataMember(Name = "season_number")]
        public int SeasonNumber { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "overview")]
        public string Overview { get; set; }

        [DataMember(Name = "still_path")]
        public string StillPath { get; set; }

        [DataMember(Name = "air_date")]
        public string AirDate { get; set; }

        [DataMember(Name = "vote_average")]
        public double VoteAverage { get; set; }

        [DataMember(Name = "runtime")]
        public int? Runtime { get; set; }
    }

    // =============================================================
    // EXTERNAL IDS
    // =============================================================
    //
    // TMDB exposes several external identifiers here; only imdb_id is
    // needed for this phase (it's what Stremio addons key streams on).

    [DataContract]
    public class TmdbExternalIds
    {
        [DataMember(Name = "imdb_id")]
        public string ImdbId { get; set; }
    }

    // =============================================================
    // VIDEO RESPONSE
    // =============================================================

    [DataContract]
    public class TmdbVideoResponse
    {
        [DataMember(Name = "results")]
        public List<TmdbVideoItem> Results { get; set; }
    }

    // =============================================================
    // VIDEO ITEM
    // =============================================================

    [DataContract]
    public class TmdbVideoItem
    {
        [DataMember(Name = "iso_639_1")]
        public string Iso6391 { get; set; }

        [DataMember(Name = "iso_3166_1")]
        public string Iso31661 { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "key")]
        public string Key { get; set; }

        [DataMember(Name = "site")]
        public string Site { get; set; }

        [DataMember(Name = "size")]
        public int Size { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "official")]
        public bool Official { get; set; }

        [DataMember(Name = "published_at")]
        public string PublishedAt { get; set; }
    }
}
