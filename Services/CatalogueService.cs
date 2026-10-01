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
    // Movies, TV and (as of Slice 17) Anime. Not Home-specific - Home's
    // six rows AND the dedicated Movies/TV pages (Featured/Popular/New
    // Releases, with genre/year filtering and skip-based pagination)
    // both call this same class. AnimeFlvCatalogueService remains the
    // separate, parallel client for AnimeFLV's own genre/search
    // catalogs, kept out of this class per "keep systems modular, don't
    // tightly couple unrelated systems."
    //
    // =============================================================
    // SLICE 17 ANIME CHANGE (Kitsu locked in as Zino's permanent anime
    // provider)
    // =============================================================
    //
    // The three ANIME methods below (GetFeaturedAnimeAsync/
    // GetPopularAnimeAsync/GetNewAnimeAsync) were previously TMDB-only
    // (honestly empty whenever no TmdbService was configured - see this
    // file's prior-slice remarks, preserved in git history). Per this
    // slice's explicit instruction ("remove the old TMDB-only Anime
    // catalogue path rather than leaving TMDB as a hidden fallback"),
    // they are now backed entirely by KitsuService - the same provider
    // AnimeBrowseView's own filtered browse uses - and no longer read
    // _tmdbService at all. TMDB remains this class's Movie/TV fallback
    // only (unchanged, out of this slice's scope).
    //
    // Kitsu has no "Featured"/"Popular"/"New" catalogs of its own (see
    // KitsuService remarks - no dedicated "trending" endpoint exists),
    // so these three names are mapped onto Kitsu's real, live-verified
    // sort keys the same way this file already maps Zino's Movie/TV
    // terminology onto Cinemeta's real catalog ids (see the SLICE 15
    // TERMINOLOGY CORRECTION remarks below):
    //   - Featured -> sort=ratingRank (Kitsu's top-rated ranking)
    //   - Popular  -> KitsuService.GetPopularAnimeAsync (sort=-userCount,
    //     left unchanged per this slice's instruction to keep that
    //     method working exactly as-is for its existing callers)
    //   - New      -> sort=-startDate (newest-airing first)
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
        private readonly KitsuService _kitsuService;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        // kitsuService is required (same as cinemetaService) - Slice 17
        // makes it the anime rows' sole backbone, exactly as
        // cinemetaService already is for Movie/TV. tmdbService remains
        // optional (Movie/TV fallback only).
        public CatalogueService(TmdbService tmdbService, CinemetaService cinemetaService, KitsuService kitsuService)
        {
            if (cinemetaService == null)
            {
                throw new ArgumentNullException(nameof(cinemetaService));
            }

            if (kitsuService == null)
            {
                throw new ArgumentNullException(nameof(kitsuService));
            }

            _tmdbService = tmdbService;
            _cinemetaService = cinemetaService;
            _kitsuService = kitsuService;
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
        // ANIME (Slice 17: Kitsu-backed - see class-level remarks above)
        // =========================================================

        public async Task<List<MediaItem>> GetFeaturedAnimeAsync(int take = DefaultTake)
        {
            string cacheKey = "anime-featured:kitsu:" + take;

            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            KitsuBrowseResult result = await _kitsuService.BrowseAnimeAsync(sort: "ratingRank", limit: take);

            List<MediaItem> items = MapKitsuItemsToMediaItems(result?.Items, take);

            SetCached(cacheKey, items);

            return items;
        }

        public async Task<List<MediaItem>> GetPopularAnimeAsync(int take = DefaultTake)
        {
            string cacheKey = "anime-popular:kitsu:" + take;

            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            List<KitsuAnimeResource> candidates = await _kitsuService.GetPopularAnimeAsync(offset: 0, limit: take);

            List<MediaItem> items = MapKitsuItemsToMediaItems(candidates, take);

            SetCached(cacheKey, items);

            return items;
        }

        public async Task<List<MediaItem>> GetNewAnimeAsync(int take = DefaultTake)
        {
            string cacheKey = "anime-new:kitsu:" + take;

            List<MediaItem> cached = TryGetCached(cacheKey);

            if (cached != null)
            {
                return cached;
            }

            KitsuBrowseResult result = await _kitsuService.BrowseAnimeAsync(sort: "-startDate", limit: take);

            List<MediaItem> items = MapKitsuItemsToMediaItems(result?.Items, take);

            SetCached(cacheKey, items);

            return items;
        }

        // =========================================================
        // KITSU -> MEDIAITEM MAPPING (Anime rows only)
        // =========================================================
        //
        // Mirrors AnimeBrowseView.xaml.cs's own MapKitsuToMediaItem
        // exactly (same StremioId "kitsu:<id>" convention, same field
        // mapping) - kept as a separate copy rather than a shared
        // static helper because the two classes have no existing common
        // base/util class for this, and duplicating one small mapping
        // function is preferable to introducing a new cross-cutting
        // dependency for it (see this project's "avoid unnecessary
        // abstraction" guidance).
        private static List<MediaItem> MapKitsuItemsToMediaItems(List<KitsuAnimeResource> items, int take)
        {
            var result = new List<MediaItem>();

            if (items == null || items.Count == 0)
            {
                return result;
            }

            foreach (KitsuAnimeResource item in items)
            {
                if (result.Count >= take)
                {
                    break;
                }

                MediaItem mapped = MapKitsuToMediaItem(item);

                if (mapped != null)
                {
                    result.Add(mapped);
                }
            }

            return result;
        }

        private static MediaItem MapKitsuToMediaItem(KitsuAnimeResource item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.Attributes == null)
            {
                return null;
            }

            string title = !string.IsNullOrWhiteSpace(item.Attributes.CanonicalTitle)
                ? item.Attributes.CanonicalTitle
                : item.Attributes.Titles?.En;

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string poster = item.Attributes.PosterImage?.Large ?? item.Attributes.PosterImage?.Medium;

            var mediaItem = new MediaItem
            {
                StremioId = "kitsu:" + item.Id,
                TmdbId = null,
                Title = title,
                Overview = item.Attributes.Synopsis,
                ReleaseDate = item.Attributes.StartDate,
                Rating = item.Attributes.AverageRating,
                Type = "Anime",
                Genres = item.Attributes.Categories?.Select(c => c.Title).ToList() ?? new List<string>(),
                PosterPath = poster ?? string.Empty,
                BackdropUrl = item.Attributes.CoverImage?.Large ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(item.Attributes.StartDate) && item.Attributes.StartDate.Length >= 4)
            {
                mediaItem.Year = item.Attributes.StartDate.Substring(0, 4);
            }

            return mediaItem;
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
