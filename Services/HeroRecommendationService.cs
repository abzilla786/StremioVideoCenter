using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    public class HeroRecommendationService
    {
        // =========================================================
        // FIELDS
        // =========================================================

        private readonly TmdbService tmdbService;

        private readonly Random random =
            new Random();

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public HeroRecommendationService(
            TmdbService tmdbService)
        {
            this.tmdbService = tmdbService;
        }

        // =========================================================
        // GET HERO RECOMMENDATIONS
        // =========================================================

        public async Task<List<HeroItem>>
            GetRecommendationsAsync()
        {
            var movieTask =
                tmdbService.GetMovieCandidatesAsync();

            var tvTask =
                tmdbService.GetTvCandidatesAsync();

            var animeTask =
                tmdbService.GetAnimeCandidatesAsync();

            await Task.WhenAll(
                movieTask,
                tvTask,
                animeTask);

            var movies =
                FilterCandidates(
                    movieTask.Result);

            var tvShows =
                FilterCandidates(
                    tvTask.Result);

            var anime =
                FilterCandidates(
                    animeTask.Result);

            var selectedCandidates =
                SelectFiveCandidates(
                    movies,
                    tvShows,
                    anime);

            return selectedCandidates;
        }

        // =========================================================
        // FILTER CANDIDATES
        // =========================================================

        private List<TmdbDiscoveryItem>
            FilterCandidates(
                List<TmdbDiscoveryItem> candidates)
        {
            if (candidates == null)
            {
                return new List<TmdbDiscoveryItem>();
            }

            return candidates
                .Where(item =>
                    item != null &&
                    item.Id > 0 &&
                    !item.Adult &&
                    item.HasPoster &&
                    item.HasBackdrop &&
                    !string.IsNullOrWhiteSpace(
                        item.DisplayTitle))
                .OrderByDescending(
                    item => item.VoteAverage)
                .ThenByDescending(
                    item => item.Popularity)
                .ToList();
        }

        // =========================================================
        // SELECT EXACTLY FIVE CANDIDATES
        // =========================================================

        private List<HeroItem>
            SelectFiveCandidates(
                List<TmdbDiscoveryItem> movies,
                List<TmdbDiscoveryItem> tvShows,
                List<TmdbDiscoveryItem> anime)
        {
            var selected =
                new List<TmdbDiscoveryItem>();

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

            // =========================================================
            // CONVERT TO HERO ITEMS
            // =========================================================

            return selected
                .Take(5)
                .Select(
                    CreateHeroItem)
                .ToList();
        }

        // =========================================================
        // ADD RANDOM CANDIDATE
        // =========================================================

        private void AddRandomCandidate(
            List<TmdbDiscoveryItem> selected,
            List<TmdbDiscoveryItem> candidates)
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
        // CREATE HERO ITEM
        // =========================================================

        private HeroItem CreateHeroItem(
            TmdbDiscoveryItem candidate)
        {
            string type =
                GetContentType(
                    candidate);

            string posterUrl =
                BuildImageUrl(
                    candidate.PosterPath,
                    "w500");

            string backdropUrl =
                BuildImageUrl(
                    candidate.BackdropPath,
                    "w1280");

            return new HeroItem
            {
                TmdbId =
                    candidate.Id,

                Title =
                    candidate.DisplayTitle,

                Type =
                    type,

                Year =
                    candidate.DisplayYear,

                Rating =
                    candidate.VoteAverage
                        .ToString("0.0"),

                Synopsis =
                    candidate.Overview,

                PosterUrl =
                    posterUrl,

                BackdropUrl =
                    backdropUrl,

                TrailerUrl =
                    string.Empty
            };
        }

        // =========================================================
        // DETERMINE CONTENT TYPE
        // =========================================================

        private string GetContentType(
            TmdbDiscoveryItem candidate)
        {
            if (!string.IsNullOrWhiteSpace(
                    candidate.Title))
            {
                return "Movie";
            }

            if (candidate.GenreIds != null &&
                candidate.GenreIds.Contains(16) &&
                string.Equals(
                    candidate.OriginalLanguage,
                    "ja",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Anime";
            }

            return "TV";
        }

        // =========================================================
        // BUILD TMDB IMAGE URL
        // =========================================================

        private string BuildImageUrl(
            string imagePath,
            string size)
        {
            if (string.IsNullOrWhiteSpace(
                    imagePath))
            {
                return string.Empty;
            }

            return
                "https://image.tmdb.org/t/p/" +
                size +
                imagePath;
        }
    }
}