using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // HERO RECOMMENDATION SERVICE
    // =============================================================
    //
    // TMDB Removal migration (approved Migration Plan, Phase 1 - Hero).
    //
    // Sources Hero candidates from Cinemeta (Movie/TV - the same
    // "top" catalogues CatalogueService's own Cinemeta fallback uses)
    // and Kitsu (Anime - GetPopularAnimeAsync, the same source
    // AnimeBrowseView/SearchView already use for Kitsu results). TMDB
    // is no longer used anywhere in this class. Kitsu was locked in
    // as Zino's permanent anime provider for the current v1
    // architecture (AniList investigated and explicitly rejected) -
    // see this project's own architecture docs for that decision.
    //
    // The selection algorithm itself (prefer one of each content type,
    // then fill randomly, five total) is unchanged from the TMDB-based
    // version - only the candidate data source changed.
    public class HeroRecommendationService
    {
        // =========================================================
        // FIELDS
        // =========================================================

        private readonly CinemetaService cinemetaService;

        private readonly KitsuService kitsuService;

        private readonly Random random =
            new Random();

        // Kitsu's own documented/enforced page[limit] maximum (see
        // KitsuService.MaxPageLimit) - used as the candidate-pool size
        // for the Anime side, matching the pool size Cinemeta's "top"
        // catalogues naturally provide per page.
        private const int KitsuCandidatePageSize = 20;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public HeroRecommendationService(
            CinemetaService cinemetaService,
            KitsuService kitsuService)
        {
            this.cinemetaService = cinemetaService;
            this.kitsuService = kitsuService;
        }

        // =========================================================
        // GET HERO RECOMMENDATIONS
        // =========================================================

        public async Task<List<HeroItem>>
            GetRecommendationsAsync()
        {
            var movieTask =
                cinemetaService.GetCatalogAsync(
                    "movie",
                    "top");

            var tvTask =
                cinemetaService.GetCatalogAsync(
                    "series",
                    "top");

            var animeTask =
                kitsuService.GetPopularAnimeAsync(
                    0,
                    KitsuCandidatePageSize);

            await Task.WhenAll(
                movieTask,
                tvTask,
                animeTask);

            var movies =
                FilterCinemetaCandidates(
                    movieTask.Result,
                    "Movie");

            var tvShows =
                FilterCinemetaCandidates(
                    tvTask.Result,
                    "TV");

            var anime =
                FilterKitsuCandidates(
                    animeTask.Result);

            var selectedCandidates =
                SelectFiveCandidates(
                    movies,
                    tvShows,
                    anime);

            return await BuildHeroItemsAsync(
                selectedCandidates);
        }

        // =========================================================
        // CANDIDATE (a lightweight, source-agnostic wrapper so
        // selection can treat Cinemeta and Kitsu candidates uniformly)
        // =========================================================

        private class HeroCandidate
        {
            public string Id;
            public string ContentType;
            public double SortRating;
            public CinemetaCatalogItem CinemetaItem;
            public KitsuAnimeResource KitsuItem;
        }

        // =========================================================
        // FILTER CINEMETA CANDIDATES (Movie/TV)
        // =========================================================
        //
        // id is ImdbId (falling back to Id, matching
        // CatalogueService.BuildMediaItemsFromCinemeta's own
        // established convention) and must start with "tt" - Cinemeta
        // catalog items are otherwise assumed to always carry a real
        // imdb id, but this is defensive, matching that same
        // convention. No HasBackdrop requirement - Cinemeta catalog
        // (preview) items never carry a background image (see
        // CinemetaCatalogItem's own remarks); backdrop is filled in
        // afterwards, per selected candidate, via GetMetaAsync.
        private static List<HeroCandidate> FilterCinemetaCandidates(
            List<CinemetaCatalogItem> items,
            string contentType)
        {
            var candidates = new List<HeroCandidate>();

            if (items == null)
            {
                return candidates;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                string id = item.ImdbId ?? item.Id;

                if (string.IsNullOrWhiteSpace(id) ||
                    !id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name) ||
                    string.IsNullOrWhiteSpace(item.Poster))
                {
                    continue;
                }

                candidates.Add(new HeroCandidate
                {
                    Id = id,
                    ContentType = contentType,
                    SortRating = ParseRating(item.ImdbRating),
                    CinemetaItem = item
                });
            }

            return candidates
                .OrderByDescending(c => c.SortRating)
                .ToList();
        }

        // =========================================================
        // FILTER KITSU CANDIDATES (Anime)
        // =========================================================

        private static List<HeroCandidate> FilterKitsuCandidates(
            List<KitsuAnimeResource> items)
        {
            var candidates = new List<HeroCandidate>();

            if (items == null)
            {
                return candidates;
            }

            foreach (var item in items)
            {
                if (item == null ||
                    string.IsNullOrWhiteSpace(item.Id) ||
                    item.Attributes == null)
                {
                    continue;
                }

                string title =
                    item.Attributes.CanonicalTitle ??
                    item.Attributes.Titles?.En;

                string poster =
                    item.Attributes.PosterImage?.Large ??
                    item.Attributes.PosterImage?.Medium;

                if (string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(poster))
                {
                    continue;
                }

                candidates.Add(new HeroCandidate
                {
                    Id = "kitsu:" + item.Id,
                    ContentType = "Anime",
                    SortRating = ParseRating(item.Attributes.AverageRating),
                    KitsuItem = item
                });
            }

            return candidates
                .OrderByDescending(c => c.SortRating)
                .ToList();
        }

        // =========================================================
        // PARSE RATING (shared - both Cinemeta's ImdbRating and
        // Kitsu's AverageRating are strings)
        // =========================================================

        private static double ParseRating(string rating)
        {
            double value;

            if (double.TryParse(
                    rating,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return value;
            }

            return 0;
        }

        // =========================================================
        // SELECT EXACTLY FIVE CANDIDATES
        // =========================================================

        private List<HeroCandidate>
            SelectFiveCandidates(
                List<HeroCandidate> movies,
                List<HeroCandidate> tvShows,
                List<HeroCandidate> anime)
        {
            var selected =
                new List<HeroCandidate>();

            // =========================================================
            // PREFER ALL THREE CATEGORIES
            // =========================================================

            AddRandomCandidate(
                selected,
                movies);

            AddRandomCandidate(
                selected,
                tvShows);

            AddRandomCandidate(
                selected,
                anime);

            // =========================================================
            // FILL REMAINING POSITIONS
            // =========================================================

            var remainingCandidates =
                movies
                    .Concat(tvShows)
                    .Concat(anime)
                    .Where(item =>
                        !selected.Any(
                            existing =>
                                existing.Id == item.Id))
                    .OrderBy(
                        item => random.Next())
                    .ToList();

            foreach (var candidate in
                     remainingCandidates)
            {
                if (selected.Count >= 5)
                {
                    break;
                }

                selected.Add(candidate);
            }

            return selected;
        }

        // =========================================================
        // ADD RANDOM CANDIDATE
        // =========================================================

        private void AddRandomCandidate(
            List<HeroCandidate> selected,
            List<HeroCandidate> candidates)
        {
            if (selected.Count >= 5 ||
                candidates == null ||
                candidates.Count == 0)
            {
                return;
            }

            var candidatePool =
                candidates
                    .Where(item =>
                        !selected.Any(
                            existing =>
                                existing.Id == item.Id))
                    .Take(10)
                    .ToList();

            if (candidatePool.Count == 0)
            {
                return;
            }

            var candidate =
                candidatePool[
                    random.Next(
                        candidatePool.Count)];

            selected.Add(candidate);
        }

        // =========================================================
        // BUILD HERO ITEMS
        // =========================================================
        //
        // Anime candidates need no extra network call - Kitsu's
        // collection response already carries CoverImage (used as the
        // backdrop) inline. Movie/TV candidates need one extra
        // GetMetaAsync call each (Cinemeta's own lightweight catalog
        // items never carry a background image - see
        // CinemetaCatalogItem's own remarks) - only for the five
        // already-selected candidates, never for the full candidate
        // pool, keeping this to at most 3 extra requests (movies/tv
        // only; anime never needs one) per Hero load.
        private async Task<List<HeroItem>> BuildHeroItemsAsync(
            List<HeroCandidate> candidates)
        {
            var heroItems = new List<HeroItem>();

            foreach (var candidate in candidates)
            {
                if (candidate.KitsuItem != null)
                {
                    heroItems.Add(
                        CreateHeroItemFromKitsu(
                            candidate.KitsuItem));

                    continue;
                }

                HeroItem heroItem =
                    CreateHeroItemFromCinemeta(
                        candidate);

                await EnrichCinemetaBackdropAsync(
                    heroItem,
                    candidate.ContentType);

                heroItems.Add(heroItem);
            }

            return heroItems;
        }

        // =========================================================
        // CREATE HERO ITEM (Cinemeta - Movie/TV)
        // =========================================================

        private static HeroItem CreateHeroItemFromCinemeta(
            HeroCandidate candidate)
        {
            CinemetaCatalogItem item =
                candidate.CinemetaItem;

            return new HeroItem
            {
                StremioId =
                    candidate.Id,

                Title =
                    item.Name,

                Type =
                    candidate.ContentType,

                Year =
                    !string.IsNullOrWhiteSpace(item.Year)
                        ? item.Year
                        : item.ReleaseInfo,

                Rating =
                    item.ImdbRating,

                Synopsis =
                    item.Description,

                PosterUrl =
                    item.Poster,

                // Filled in afterwards by EnrichCinemetaBackdropAsync -
                // Cinemeta's own catalog (preview) shape never carries
                // a background image.
                BackdropUrl =
                    string.Empty,

                TrailerUrl =
                    string.Empty,

                Genres =
                    item.Genres ?? new List<string>()
            };
        }

        // =========================================================
        // CREATE HERO ITEM (Kitsu - Anime)
        // =========================================================

        private static HeroItem CreateHeroItemFromKitsu(
            KitsuAnimeResource item)
        {
            KitsuAnimeAttributes attributes =
                item.Attributes;

            string year =
                !string.IsNullOrWhiteSpace(attributes.StartDate) &&
                attributes.StartDate.Length >= 4
                    ? attributes.StartDate.Substring(0, 4)
                    : string.Empty;

            return new HeroItem
            {
                StremioId =
                    "kitsu:" + item.Id,

                Title =
                    attributes.CanonicalTitle ??
                    attributes.Titles?.En,

                Type =
                    "Anime",

                Year =
                    year,

                Rating =
                    attributes.AverageRating,

                Synopsis =
                    attributes.Synopsis,

                PosterUrl =
                    attributes.PosterImage?.Large ??
                    attributes.PosterImage?.Medium,

                BackdropUrl =
                    attributes.CoverImage?.Large ??
                    attributes.CoverImage?.Small ??
                    string.Empty,

                TrailerUrl =
                    string.Empty,

                // Kitsu categories are a relationship, not resolved by
                // GetPopularAnimeAsync (only BrowseAnimeAsync requests
                // include=categories and resolves them) - empty here,
                // matching this slice's scope (Hero's own genre display
                // is not part of the Anime Browse filter work).
                Genres =
                    new List<string>()
            };
        }

        // =========================================================
        // ENRICH CINEMETA BACKDROP
        // =========================================================
        //
        // One extra Cinemeta /meta/ call per selected Movie/TV
        // candidate, to get the background image the lightweight
        // catalog item didn't carry. Defensive try/catch backstop in
        // addition to CinemetaService.GetMetaAsync's own fail-soft
        // (null-on-failure) contract - a Hero card should never be
        // left half-built by a single enrichment failure.
        private async Task EnrichCinemetaBackdropAsync(
            HeroItem heroItem,
            string contentType)
        {
            try
            {
                string cinemetaType =
                    string.Equals(contentType, "Movie", StringComparison.OrdinalIgnoreCase)
                        ? "movie"
                        : "series";

                CinemetaMeta meta =
                    await cinemetaService.GetMetaAsync(
                        cinemetaType,
                        heroItem.StremioId);

                if (meta != null &&
                    !string.IsNullOrWhiteSpace(meta.Background))
                {
                    heroItem.BackdropUrl =
                        meta.Background;
                }
            }
            catch
            {
                // Leave BackdropUrl empty - HeroItem.LoadBackdropAsync
                // already no-ops on an empty url, so the card still
                // renders (without a backdrop image) rather than
                // failing the whole Hero load.
            }
        }
    }
}
