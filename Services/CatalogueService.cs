using StremioVideoCenter.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // CATALOGUE SERVICE
    // =============================================================
    //
    // Real Catalogue & Content Foundation slice (Slice 13), revised by
    // Slice 15 (Search, Discovery, Home & Local Media Architecture).
    //
    // A reusable, UI-independent source of real catalogue rows/pages for
    // Movies and TV. Not Home-specific - Home's six rows AND the
    // dedicated Movies/TV pages (Featured/Popular/New Releases, with
    // genre/year filtering and skip-based pagination) both call this
    // same class. Anime rows remain TMDB-only (see ANIME remarks below) -
    // AnimeFlvCatalogueService is the separate, parallel client for
    // AnimeFLV's own genre/search catalogs, kept out of this class per
    // "keep systems modular, don't tightly couple unrelated systems."
    //
    // =============================================================
    // SLICE 15 TERMINOLOGY CORRECTION (supersedes Slice 13's mapping)
    // =============================================================
    //
    // Slice 13 originally mapped Cinemeta's catalogs onto invented labels
    // "Top 10" / "Trending" / "Recently Released". Slice 15's own
    // specification identified this as inaccurate (Cinemeta has no
    // "Trending" or "Top 10" concept at all - see
    // claude/Cinemeta_Capability_Audit.md) and requires provider-accurate
    // labels instead:
    //   - Zino "Featured"  -> Cinemeta "imdbRating" (a genuine IMDb-
    //     rating-sorted ranking - Cinemeta's own manifest calls this
    //     catalog "Featured").
    //   - Zino "Popular"   -> Cinemeta "top" (Cinemeta's own manifest
    //     calls this catalog "Popular").
    //   - Zino "New"       -> Cinemeta "year" (Cinemeta's own manifest
    //     calls this catalog "New"), with the requested year as the
    //     REQUIRED genre extra (live-verified this slice: Cinemeta's own
    //     manifest declares "genre" as required on this catalog, with an
    //     "options" list of literal year strings, not genre names).
    // "Top 10"/"Trending"/"Recently Released" labels are fully removed
    // from this codebase as of this slice. New Releases also moves off
    // Home entirely this slice (dedicated Movies/TV pages only) - see
    // HomeViewModel/StremioVideoCenterView.
    //
    // =============================================================
    // GENRE / YEAR OPTIONS (live-verified this slice)
    // =============================================================
    //
    // Fetched directly from Cinemeta's own manifest.json ("extra.options"
    // on each catalog) this slice - not invented, not TMDB's genre list.
    // Movie and series genre option sets differ slightly (series adds
    // Reality-TV/Talk-Show/Game-Show); year options are catalog-specific
    // too (series only goes back to 1960, movies to 1920). Exposed here
    // as the single source of truth for both Home's implicit "current
    // year" New-Releases query (page-only) and the dedicated Movies/TV
    // pages' filter dropdowns, so a filter is only ever offered when the
    // active provider (Cinemeta) actually supports that exact value -
    // never invented, never a superset guess.
    //
    // Cinemeta declares NO rating/minimum-rating extra on any catalog -
    // a rating filter is therefore not offered anywhere in this codebase
    // (see the dedicated Movies/TV page remarks) rather than faked with
    // client-side filtering over a partial result page.
    public class CatalogueService
    {
        // =========================================================
        // GENRE OPTIONS (Cinemeta-verified)
        // =========================================================

        public static readonly IReadOnlyList<string> MovieGenres = new List<string>
        {
            "Action", "Adventure", "Animation", "Biography", "Comedy", "Crime",
            "Documentary", "Drama", "Family", "Fantasy", "History", "Horror",
            "Mystery", "Romance", "Sci-Fi", "Sport", "Thriller", "War", "Western"
        };

        public static readonly IReadOnlyList<string> SeriesGenres = new List<string>
        {
            "Action", "Adventure", "Animation", "Biography", "Comedy", "Crime",
            "Documentary", "Drama", "Family", "Fantasy", "History", "Horror",
            "Mystery", "Romance", "Sci-Fi", "Sport", "Thriller", "War", "Western",
            "Reality-TV", "Talk-Show", "Game-Show"
        };

        // =========================================================
        // YEAR OPTIONS (Cinemeta-verified, newest first)
        // =========================================================

        public static IReadOnlyList<int> MovieYears
        {
            get { return BuildYearRange(1920); }
        }

        public static IReadOnlyList<int> SeriesYears
        {
            get { return BuildYearRange(1960); }
        }

        private static List<int> BuildYearRange(int earliestYear)
        {
            var years = new List<int>();

            for (int year = DateTime.UtcNow.Year; year >= earliestYear; year--)
            {
                years.Add(year);
            }

            return years;
        }

        // =========================================================
        // CONFIGURATION
        // =========================================================

        // Home's default row size. Dedicated Movies/TV pages request
        // larger pages via the "take" parameter on every method below.
        private const int DefaultTake = 25;

        // Bounded, same defensive reasoning as
        // PlaybackSourceAggregationService's Real-Debrid attempt cap -
        // scales with the requested page size but never fans out
        // unboundedly against TMDB.
        private const int MaxConcurrentDetailLookups = 5;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>();

        private class CacheEntry
        {
            public DateTime FetchedAtUtc;
            public List<MediaItem> Items;
        }

        // =========================================================
        // FIELDS
        // =========================================================

        private readonly TmdbService _tmdbService;
        private readonly CinemetaService _cinemetaService;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public CatalogueService(TmdbService tmdbService, CinemetaService cinemetaService)
        {
            if (cinemetaService == null)
            {
                throw new ArgumentNullException(nameof(cinemetaService));
            }

            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
        }

        // =========================================================
        // MOVIES
        // =========================================================

        public Task<List<MediaItem>> GetFeaturedMoviesAsync(int take = DefaultTake, int skip = 0, string genre = null)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("movies-featured", genre, skip, take),
                "movie", "imdbRating", BuildExtraSegment(genre, skip),
                "Movie", isTv: false, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetTopRatedMoviesAsync()) : null);
        }

        public Task<List<MediaItem>> GetPopularMoviesAsync(int take = DefaultTake, int skip = 0, string genre = null)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("movies-popular", genre, skip, take),
                "movie", "top", BuildExtraSegment(genre, skip),
                "Movie", isTv: false, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetTrendingMoviesAsync()) : null);
        }

        // year is REQUIRED (Cinemeta's own manifest - see class remarks).
        public Task<List<MediaItem>> GetNewMoviesAsync(int year, int take = DefaultTake, int skip = 0)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("movies-new-" + year, null, skip, take),
                "movie", "year", BuildExtraSegment(year.ToString(), skip),
                "Movie", isTv: false, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetNowPlayingMoviesAsync()) : null);
        }

        // =========================================================
        // TV SHOWS
        // =========================================================

        public Task<List<MediaItem>> GetFeaturedTvShowsAsync(int take = DefaultTake, int skip = 0, string genre = null)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("tv-featured", genre, skip, take),
                "series", "imdbRating", BuildExtraSegment(genre, skip),
                "TV", isTv: true, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetTopRatedTvAsync()) : null);
        }

        public Task<List<MediaItem>> GetPopularTvShowsAsync(int take = DefaultTake, int skip = 0, string genre = null)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("tv-popular", genre, skip, take),
                "series", "top", BuildExtraSegment(genre, skip),
                "TV", isTv: true, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetTrendingTvAsync()) : null);
        }

        public Task<List<MediaItem>> GetNewTvShowsAsync(int year, int take = DefaultTake, int skip = 0)
        {
            return GetCatalogueRowAsync(
                BuildCacheKey("tv-new-" + year, null, skip, take),
                "series", "year", BuildExtraSegment(year.ToString(), skip),
                "TV", isTv: true, take: take,
                tmdbFallbackFetch: skip == 0 ? (Func<Task<List<TmdbDiscoveryItem>>>)(() => _tmdbService.GetOnTheAirTvAsync()) : null);
        }

        // =========================================================
        // ANIME
        // =========================================================
        //
        // Unchanged logic from Slice 13's GetTopAnimeAsync/
        // GetTrendingAnimeAsync/GetRecentlyReleasedAnimeAsync - only
        // renamed to match the Featured/Popular/New terminology used
        // everywhere else in this slice, and "take" is now honoured
        // instead of the old fixed RowSize=10. Still TMDB-only/honestly-
        // empty when no TmdbService is configured - see the class-level
        // remarks in the previous slice's version of this file for why
        // (no verified Cinemeta or AnimeFLV *unfiltered* anime catalogue
        // exists - AnimeFLV's real, live-verified catalogue this slice
        // turned out to require either a search term or a genre for
        // EVERY catalog it exposes, so it cannot honestly back a
        // "Featured"/"Popular" ranked row either - see
        // AnimeFlvCatalogueService and claude/Slice15_*.md).

        public Task<List<MediaItem>> GetFeaturedAnimeAsync(int take = DefaultTake)
        {
            if (_tmdbService == null)
            {
                return Task.FromResult(new List<MediaItem>());
            }

            return GetCachedAsync(
                "anime-featured:tmdb:" + take,
                () => _tmdbService.GetTopRatedAnimeAsync(),
                isTv: true,
                type: "Anime",
                take: take);
        }

        public async Task<List<MediaItem>> GetPopularAnimeAsync(int take = DefaultTake)
        {
            if (_tmdbService == null)
            {
                return new List<MediaItem>();
            }

            string cacheKey = "anime-popular:tmdb:" + take;

            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            var candidates = new List<TmdbDiscoveryItem>();

            for (int page = 1; page <= 3; page++)
            {
                List<TmdbDiscoveryItem> pageResults = await _tmdbService.GetTrendingTvAsync(page);

                if (pageResults == null || pageResults.Count == 0)
                {
                    break;
                }

                candidates.AddRange(pageResults);
            }

            var seenTmdbIds = new HashSet<int>();

            List<TmdbDiscoveryItem> animeOnly =
                candidates
                    .Where(IsAnimeCandidate)
                    .Where(item => seenTmdbIds.Add(item.Id))
                    .ToList();

            List<MediaItem> items = await BuildMediaItemsAsync(animeOnly, isTv: true, type: "Anime", take: take);

            SetCached(cacheKey, items);

            return items;
        }

        public Task<List<MediaItem>> GetNewAnimeAsync(int take = DefaultTake)
        {
            if (_tmdbService == null)
            {
                return Task.FromResult(new List<MediaItem>());
            }

            return GetCachedAsync(
                "anime-new:tmdb:" + take,
                () => _tmdbService.GetRecentlyAiredAnimeAsync(),
                isTv: true,
                type: "Anime",
                take: take);
        }

        // =========================================================
        // ANIME CANDIDATE FILTER
        // =========================================================

        private static bool IsAnimeCandidate(TmdbDiscoveryItem candidate)
        {
            return candidate != null &&
                   candidate.GenreIds != null &&
                   candidate.GenreIds.Contains(16) &&
                   string.Equals(candidate.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================
        // EXTRA-SEGMENT / CACHE-KEY BUILDING
        // =========================================================
        //
        // Stremio's own protocol joins multiple extras into one path
        // segment with "&" (e.g. "genre=Action&skip=20") - live-verified
        // working correctly this slice (both genre+skip and required-
        // year+skip combinations were tested directly against Cinemeta;
        // see claude/Slice15_Search_Discovery_Home_LocalMedia.md for the
        // exact requests/responses). A small amount of noise was observed
        // in Cinemeta's own filtered results (very occasionally an item
        // whose own genres/year don't exactly match the requested filter)
        // - documented as a Cinemeta data-quality limitation, not a bug
        // in this combination logic.
        private static string BuildExtraSegment(string genreOrYear, int skip)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(genreOrYear))
            {
                parts.Add("genre=" + Uri.EscapeDataString(genreOrYear));
            }

            if (skip > 0)
            {
                parts.Add("skip=" + skip);
            }

            return parts.Count == 0 ? null : string.Join("&", parts);
        }

        private static string BuildCacheKey(string baseKey, string genre, int skip, int take)
        {
            return baseKey + ":" + (genre ?? string.Empty) + ":" + skip + ":" + take;
        }

        // =========================================================
        // CATALOGUE ROW FETCH (Cinemeta-first, TMDB fallback)
        // =========================================================
        //
        // TMDB fallback is only attempted for the first page (skip==0,
        // tmdbFallbackFetch passed as null otherwise by every caller
        // above) - TMDB's own top_rated/trending/now_playing delegates
        // have no skip/page parameter threaded through them from this
        // class today (unchanged from Slice 13), so a paginated request
        // that found Cinemeta empty would otherwise silently re-fetch
        // TMDB's fixed first page on every "Load More" click. Honest
        // empty result instead - a real, documented provider-fallback
        // limitation rather than a duplicate-content bug.
        private async Task<List<MediaItem>> GetCatalogueRowAsync(
            string cacheKey,
            string cinemetaType,
            string catalogId,
            string extraSegment,
            string mediaItemType,
            bool isTv,
            int take,
            Func<Task<List<TmdbDiscoveryItem>>> tmdbFallbackFetch)
        {
            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            List<CinemetaCatalogItem> catalogItems =
                await _cinemetaService.GetCatalogAsync(cinemetaType, catalogId, extraSegment);

            List<MediaItem> items = BuildMediaItemsFromCinemeta(catalogItems, mediaItemType, take);

            if (items.Count == 0 && _tmdbService != null && tmdbFallbackFetch != null)
            {
                List<TmdbDiscoveryItem> candidates = await tmdbFallbackFetch();

                items = await BuildMediaItemsAsync(candidates, isTv, mediaItemType, take);
            }

            SetCached(cacheKey, items);

            return items;
        }

        // =========================================================
        // TMDB-ONLY CACHE-AWARE FETCH (Anime rows only)
        // =========================================================

        private async Task<List<MediaItem>> GetCachedAsync(
            string cacheKey,
            Func<Task<List<TmdbDiscoveryItem>>> fetchCandidates,
            bool isTv,
            string type,
            int take)
        {
            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            List<TmdbDiscoveryItem> candidates = await fetchCandidates();

            List<MediaItem> items = await BuildMediaItemsAsync(candidates, isTv, type, take);

            SetCached(cacheKey, items);

            return items;
        }

        private static List<MediaItem> TryGetCached(string cacheKey)
        {
            CacheEntry entry;

            if (_cache.TryGetValue(cacheKey, out entry) &&
                DateTime.UtcNow - entry.FetchedAtUtc < CacheTtl)
            {
                return entry.Items;
            }

            return null;
        }

        private static void SetCached(string cacheKey, List<MediaItem> items)
        {
            _cache[cacheKey] = new CacheEntry
            {
                FetchedAtUtc = DateTime.UtcNow,
                Items = items
            };
        }

        // =========================================================
        // BUILD MEDIA ITEMS FROM CINEMETA
        // =========================================================

        private static List<MediaItem> BuildMediaItemsFromCinemeta(
            List<CinemetaCatalogItem> catalogItems,
            string mediaItemType,
            int take)
        {
            var result = new List<MediaItem>();

            if (catalogItems == null || catalogItems.Count == 0)
            {
                return result;
            }

            foreach (CinemetaCatalogItem item in catalogItems)
            {
                if (result.Count >= take)
                {
                    break;
                }

                if (item == null)
                {
                    continue;
                }

                string imdbId =
                    !string.IsNullOrWhiteSpace(item.ImdbId)
                        ? item.ImdbId
                        : item.Id;

                if (string.IsNullOrWhiteSpace(imdbId) ||
                    !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name) ||
                    string.IsNullOrWhiteSpace(item.Poster))
                {
                    continue;
                }

                result.Add(new MediaItem
                {
                    StremioId = imdbId,
                    TmdbId = null,
                    Title = item.Name,
                    Overview = item.Description,
                    Year = !string.IsNullOrWhiteSpace(item.Year)
                        ? item.Year
                        : item.ReleaseInfo,
                    ReleaseDate = item.Released,
                    Rating = item.ImdbRating,
                    Type = mediaItemType,
                    Genres = item.Genres ?? new List<string>(),
                    PosterPath = item.Poster,
                    BackdropUrl = string.Empty
                });
            }

            return result;
        }

        // =========================================================
        // BUILD MEDIA ITEMS (candidates -> real-id MediaItems)
        // =========================================================

        private async Task<List<MediaItem>> BuildMediaItemsAsync(
            List<TmdbDiscoveryItem> candidates,
            bool isTv,
            string type,
            int take)
        {
            var result = new List<MediaItem>();

            if (candidates == null || candidates.Count == 0)
            {
                return result;
            }

            // Wider candidate pool than "take" so skipping items with no
            // IMDb mapping rarely leaves a row short - scales with the
            // requested page size, capped to avoid an unbounded TMDB
            // detail-lookup fan-out for a very large "take".
            int poolSize = Math.Min(Math.Max(take * 2, 20), 60);

            List<TmdbDiscoveryItem> pool =
                candidates
                    .Where(item =>
                        item != null &&
                        item.Id > 0 &&
                        !item.Adult &&
                        item.HasPoster &&
                        !string.IsNullOrWhiteSpace(item.DisplayTitle))
                    .Take(poolSize)
                    .ToList();

            using (var throttle = new SemaphoreSlim(MaxConcurrentDetailLookups))
            {
                var lookupTasks = pool
                    .Select(candidate => ResolveMediaItemAsync(candidate, isTv, type, throttle))
                    .ToList();

                MediaItem[] resolved = await Task.WhenAll(lookupTasks);

                foreach (MediaItem item in resolved)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (result.Count >= take)
                    {
                        break;
                    }

                    result.Add(item);
                }
            }

            return result;
        }

        // =========================================================
        // RESOLVE ONE MEDIA ITEM (details lookup for real IMDb id)
        // =========================================================

        private async Task<MediaItem> ResolveMediaItemAsync(
            TmdbDiscoveryItem candidate,
            bool isTv,
            string type,
            SemaphoreSlim throttle)
        {
            await throttle.WaitAsync();

            try
            {
                TmdbDetailsItem details = isTv
                    ? await _tmdbService.GetTvDetailsAsync(candidate.Id)
                    : await _tmdbService.GetMovieDetailsAsync(candidate.Id);

                string imdbId = details?.ImdbId;

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    return null;
                }

                return new MediaItem
                {
                    StremioId = imdbId,
                    TmdbId = candidate.Id,
                    Title = candidate.DisplayTitle,
                    Overview = candidate.Overview,
                    Year = candidate.DisplayYear,
                    ReleaseDate = isTv ? candidate.FirstAirDate : candidate.ReleaseDate,
                    Rating = candidate.VoteAverage.ToString("0.0"),
                    Type = type,
                    Genres = MapGenreIds(candidate.GenreIds),
                    PosterPath = BuildImageUrl(candidate.PosterPath, "w342"),
                    BackdropUrl = BuildImageUrl(candidate.BackdropPath, "w1280")
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                throttle.Release();
            }
        }

        // =========================================================
        // GENRE ID -> NAME MAPPING (TMDB fallback path only)
        // =========================================================

        private static readonly Dictionary<int, string> GenreNamesById =
            new Dictionary<int, string>
            {
                { 28, "Action" }, { 12, "Adventure" }, { 16, "Animation" }, { 35, "Comedy" },
                { 80, "Crime" }, { 99, "Documentary" }, { 18, "Drama" }, { 10751, "Family" },
                { 14, "Fantasy" }, { 36, "History" }, { 27, "Horror" }, { 10402, "Music" },
                { 9648, "Mystery" }, { 10749, "Romance" }, { 878, "Science Fiction" },
                { 10770, "TV Movie" }, { 53, "Thriller" }, { 10752, "War" }, { 37, "Western" },
                { 10759, "Action & Adventure" }, { 10762, "Kids" }, { 10763, "News" },
                { 10764, "Reality" }, { 10765, "Sci-Fi & Fantasy" }, { 10766, "Soap" },
                { 10767, "Talk" }, { 10768, "War & Politics" }
            };

        private static List<string> MapGenreIds(List<int> genreIds)
        {
            if (genreIds == null || genreIds.Count == 0)
            {
                return new List<string>();
            }

            var names = new List<string>();

            foreach (int id in genreIds)
            {
                string name;

                if (GenreNamesById.TryGetValue(id, out name) && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        // =========================================================
        // BUILD TMDB IMAGE URL
        // =========================================================

        private static string BuildImageUrl(string imagePath, string size)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return string.Empty;
            }

            return "https://image.tmdb.org/t/p/" + size + imagePath;
        }
    }
}
