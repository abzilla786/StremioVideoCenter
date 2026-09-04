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
    // =========================================================
    // TMDB SERVICE
    // =========================================================
    //
    // Handles authenticated communication with TMDB.
    //
    // This service currently provides:
    //
    // 1. Connection testing
    // 2. Movie discovery
    // 3. TV discovery
    // 4. Anime discovery
    //
    // The recommendation engine will be built separately
    // on top of these candidate pools.
    //
    public class TmdbService
    {
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
        // DISCOVER MOVIES
        // =========================================================
        //
        // Retrieves popular movie candidates.
        //
        // Adult content is excluded.
        // Videos are excluded from the discovery response because
        // trailer data will be retrieved later when we validate
        // individual candidates.
        //
        public async Task<List<TmdbDiscoveryItem>>
            GetMovieCandidatesAsync(int page = 1)
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
                await GetDiscoveryResponseAsync(endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // DISCOVER TV SHOWS
        // =========================================================
        //
        // Retrieves popular TV show candidates.
        //
        // Adult content is excluded and a minimum vote count
        // is used to prevent extremely obscure results from
        // dominating the candidate pool.
        //
        public async Task<List<TmdbDiscoveryItem>>
            GetTvCandidatesAsync(int page = 1)
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
                await GetDiscoveryResponseAsync(endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // DISCOVER ANIME
        // =========================================================
        //
        // TMDB does not provide a separate "anime" endpoint.
        //
        // We therefore use TMDB's TV discovery system and combine:
        //
        // Animation genre
        // Japanese origin country
        //
        // This gives us a strong initial anime candidate pool.
        //
        public async Task<List<TmdbDiscoveryItem>>
            GetAnimeCandidatesAsync(int page = 1)
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
                await GetDiscoveryResponseAsync(endpoint);

            return response?.Results ??
                   new List<TmdbDiscoveryItem>();
        }

        // =========================================================
        // GET DISCOVERY RESPONSE
        // =========================================================

        private async Task<TmdbDiscoveryResponse>
            GetDiscoveryResponseAsync(string endpoint)
        {
            string json =
                await GetAsync(endpoint);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return Deserialize<TmdbDiscoveryResponse>(json);
        }

        // =========================================================
        // AUTHENTICATED GET REQUEST
        // =========================================================
        //
        // All TMDB requests pass through this method.
        //
        // This guarantees that the configured user's
        // Authorization Bearer token is applied consistently.
        //
        private async Task<string> GetAsync(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException(
                    "TMDB API credential has not been configured.");
            }

            using (var client = new HttpClient())
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

        private T Deserialize<T>(string json)
        {
            var serializer =
                new DataContractJsonSerializer(
                    typeof(T));

            using (var stream =
                new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }


    // =========================================================
    // TMDB DISCOVERY RESPONSE
    // =========================================================

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


    // =========================================================
    // TMDB DISCOVERY ITEM
    // =========================================================
    //
    // Unified representation of a TMDB movie or TV result.
    //
    // We will later convert this into our application's
    // HeroItem model after the recommendation system has
    // finished validating the candidate.
    //
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

        // =========================================================
        // DISPLAY TITLE
        // =========================================================

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

        // =========================================================
        // DISPLAY YEAR
        // =========================================================

        public string DisplayYear
        {
            get
            {
                string date =
                    !string.IsNullOrWhiteSpace(ReleaseDate)
                        ? ReleaseDate
                        : FirstAirDate;

                if (string.IsNullOrWhiteSpace(date) ||
                    date.Length < 4)
                {
                    return string.Empty;
                }

                return date.Substring(0, 4);
            }
        }

        // =========================================================
        // ARTWORK AVAILABILITY
        // =========================================================

        public bool HasPoster
        {
            get
            {
                return !string.IsNullOrWhiteSpace(
                    PosterPath);
            }
        }

        public bool HasBackdrop
        {
            get
            {
                return !string.IsNullOrWhiteSpace(
                    BackdropPath);
            }
        }
    }
}