using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;

namespace StremioVideoCenter.ViewModels
{
    // =============================================================
    // HOME VIEW MODEL (restructured by Slice 15)
    // =============================================================
    //
    // Slice 15 (Search, Discovery, Home & Local Media Architecture)
    // replaces the old Top 10 / Trending / Recently Released structure
    // (nine rows across Movies/TV/Anime, plus an unrelated "Recently
    // Added" row that was never wired to real data) with the revised
    // Home structure: HERO -> [MY LIST if populated] ->
    // [CONTINUE WATCHING if populated] -> FEATURED MOVIES -> POPULAR
    // MOVIES -> FEATURED TV -> POPULAR TV -> FEATURED ANIME -> POPULAR
    // ANIME. New Releases is no longer a Home row (moved to the
    // dedicated Movies/TV pages only - see CatalogueBrowseView).
    //
    // ContinueWatching/MyList are an architectural CONTRACT only this
    // slice, not real functionality - Slice 16 (Watch State) and
    // Slice 17 (My List) are what will ever populate these collections.
    // They start empty (the old hardcoded test entries - a dummy "Demon
    // Slayer"/"The Last of Us" continue-watching pair and a dummy
    // "Interstellar"/"One Piece" My List pair - are removed entirely,
    // per this slice's explicit "no dummy data visible on production
    // Home" instruction) and HasContinueWatching/HasMyList expose
    // whether each collection is currently non-empty, so
    // StremioVideoCenterView.xaml can bind each section's Visibility to
    // collapse it completely (no empty carousel, no reserved space) - see
    // those two properties below.
    public class HomeViewModel : INotifyPropertyChanged
    {
        // =========================================================
        // HERO CAROUSEL
        // =========================================================

        public ObservableCollection<HeroItem> HeroItems { get; }
            = new ObservableCollection<HeroItem>();


        // =========================================================
        // CURRENT HERO INDEX
        // =========================================================

        private int _currentHeroIndex;

        public int CurrentHeroIndex
        {
            get
            {
                return _currentHeroIndex;
            }

            set
            {
                if (_currentHeroIndex == value)
                {
                    return;
                }

                _currentHeroIndex = value;

                OnPropertyChanged(nameof(CurrentHeroIndex));
                OnPropertyChanged(nameof(CurrentHeroItem));
            }
        }


        // =========================================================
        // CURRENT HERO ITEM
        // =========================================================

        public HeroItem CurrentHeroItem
        {
            get
            {
                if (HeroItems.Count == 0)
                {
                    return null;
                }

                if (CurrentHeroIndex < 0 || CurrentHeroIndex >= HeroItems.Count)
                {
                    return HeroItems[0];
                }

                return HeroItems[CurrentHeroIndex];
            }
        }


        // =========================================================
        // CONDITIONAL SECTIONS: CONTINUE WATCHING / MY LIST
        // =========================================================
        //
        // Architecture-only this slice (see class remarks) - both
        // collections start empty and nothing in this slice ever adds to
        // them. HasContinueWatching/HasMyList exist purely so
        // StremioVideoCenterView.xaml can bind each MediaSection's
        // Visibility and collapse it entirely while empty, satisfying
        // "no empty carousel, no placeholder cards, no reserved vertical
        // space" without any view-side polling - the CollectionChanged
        // subscriptions below keep these two flags accurate the moment
        // Slice 16/17 ever start adding real items.

        public ObservableCollection<MediaItem> ContinueWatching { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> MyList { get; }
            = new ObservableCollection<MediaItem>();

        private bool _hasContinueWatching;

        public bool HasContinueWatching
        {
            get { return _hasContinueWatching; }

            private set
            {
                if (_hasContinueWatching == value)
                {
                    return;
                }

                _hasContinueWatching = value;
                OnPropertyChanged(nameof(HasContinueWatching));
            }
        }

        private bool _hasMyList;

        public bool HasMyList
        {
            get { return _hasMyList; }

            private set
            {
                if (_hasMyList == value)
                {
                    return;
                }

                _hasMyList = value;
                OnPropertyChanged(nameof(HasMyList));
            }
        }


        // =========================================================
        // REAL CATALOGUE ROWS (Slice 15: Featured / Popular)
        // =========================================================
        //
        // Six rows total - two per group (Movies/TV/Anime). Replaces the
        // previous nine Top10/Trending/RecentlyReleased rows entirely;
        // "New"/Recently-Released content is no longer shown on Home at
        // all (see CatalogueBrowseView for the dedicated Movies/TV pages
        // that now carry it). Up to 25 items per row (see
        // CatalogueService's DefaultTake) rather than the previous
        // fixed 10.

        public ObservableCollection<MediaItem> FeaturedMovies { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> PopularMovies { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> FeaturedShows { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> PopularShows { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> FeaturedAnime { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> PopularAnime { get; }
            = new ObservableCollection<MediaItem>();


        // =========================================================
        // PROPERTY CHANGED EVENT
        // =========================================================

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public HomeViewModel()
        {
            ContinueWatching.CollectionChanged += ContinueWatching_CollectionChanged;
            MyList.CollectionChanged += MyList_CollectionChanged;

            LoadHeroFallbackData();
        }

        private void ContinueWatching_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            HasContinueWatching = ContinueWatching.Count > 0;
        }

        private void MyList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            HasMyList = MyList.Count > 0;
        }


        // =========================================================
        // LOAD HERO RECOMMENDATIONS
        // =========================================================

        public async Task LoadHeroRecommendationsAsync(HeroRecommendationService recommendationService)
        {
            if (recommendationService == null)
            {
                return;
            }

            try
            {
                var recommendations = await recommendationService.GetRecommendationsAsync();

                if (recommendations == null || recommendations.Count == 0)
                {
                    return;
                }

                HeroItems.Clear();

                foreach (HeroItem heroItem in recommendations)
                {
                    HeroItems.Add(heroItem);
                }

                CurrentHeroIndex = 0;

                OnPropertyChanged(nameof(CurrentHeroItem));
            }
            catch
            {
                // The existing fallback HeroItems remain available.
            }
        }


        // =========================================================
        // LOAD REAL CATALOGUE (Slice 15: Featured / Popular)
        // =========================================================
        //
        // Each of the six rows is fetched and swapped in independently -
        // one row failing (a transient Cinemeta/TMDB error) never blanks
        // a row that succeeded, and a row that fails simply keeps
        // whatever it already had (empty on first load, or last
        // session's data on a refresh) - same defensive shape as before
        // this slice.

        public async Task LoadCatalogueAsync(CatalogueService catalogueService)
        {
            if (catalogueService == null)
            {
                return;
            }

            await LoadRowAsync(FeaturedMovies, () => catalogueService.GetFeaturedMoviesAsync());
            await LoadRowAsync(PopularMovies, () => catalogueService.GetPopularMoviesAsync());

            await LoadRowAsync(FeaturedShows, () => catalogueService.GetFeaturedTvShowsAsync());
            await LoadRowAsync(PopularShows, () => catalogueService.GetPopularTvShowsAsync());

            await LoadRowAsync(FeaturedAnime, () => catalogueService.GetFeaturedAnimeAsync());
            await LoadRowAsync(PopularAnime, () => catalogueService.GetPopularAnimeAsync());
        }

        private async Task LoadRowAsync(
            ObservableCollection<MediaItem> targetCollection,
            Func<Task<List<MediaItem>>> fetchRow)
        {
            try
            {
                List<MediaItem> items = await fetchRow();

                if (items == null || items.Count == 0)
                {
                    // Genuinely empty is a real, honest result - the
                    // previous content (if any) is left in place rather
                    // than cleared, so a row never flashes away on a
                    // transient hiccup.
                    return;
                }

                targetCollection.Clear();

                foreach (MediaItem item in items)
                {
                    targetCollection.Add(item);
                }
            }
            catch
            {
                // This row keeps whatever it already had - see method
                // remarks.
            }
        }


        // =========================================================
        // HERO FALLBACK DATA
        // =========================================================
        //
        // Hero is unaffected by this slice's catalogue restructure - it
        // remains HeroRecommendationService's own TMDB-based
        // recommendation feed, with this fixed fallback shown until that
        // loads (or if it never configures). No catalogue/Continue-
        // Watching/My-List dummy data is seeded here any more (see class
        // remarks) - only the three pre-existing Hero fallback slides,
        // unchanged from before this slice.

        private void LoadHeroFallbackData()
        {
            HeroItems.Add(new HeroItem
            {
                Title = "The Last of Us",
                Type = "TV",
                Year = "2025",
                Rating = "8.8",
                Synopsis =
                    "Joel and Ellie continue their journey through a dangerous post-apocalyptic world, where difficult choices and unexpected threats test their relationship and their will to survive.",
                PosterUrl =
                    "https://image.tmdb.org/t/p/w342/uKvVjHNqB5VmOrdxqAt2F7J78ED.jpg",
                BackdropUrl =
                    "https://image.tmdb.org/t/p/w1280/9l1eZiJHmhr5jIlthMdJN5WYoff.jpg"
            });

            HeroItems.Add(new HeroItem
            {
                Title = "Interstellar",
                Type = "Movie",
                Year = "2014",
                Rating = "8.7",
                Synopsis =
                    "A team of explorers travel through a wormhole in space in an attempt to ensure humanity's survival, facing extraordinary worlds and challenges far beyond Earth.",
                PosterUrl =
                    "https://image.tmdb.org/t/p/w342/gEU2QniE6E77NI6lCU6MxlNBvIx.jpg",
                BackdropUrl =
                    "https://image.tmdb.org/t/p/w1280/xJHokMbljvjADYdit5fK4wFro0c.jpg"
            });

            HeroItems.Add(new HeroItem
            {
                Title = "Demon Slayer",
                Type = "Anime",
                Year = "2024",
                Rating = "8.7",
                Synopsis =
                    "Tanjiro and the Demon Slayer Corps face increasingly powerful enemies as they continue their fight to protect humanity and uncover the secrets surrounding the demons.",
                PosterUrl =
                    "https://image.tmdb.org/t/p/w342/xUfRZu2mi8jH6SzQEJGP6tjBuYj.jpg",
                BackdropUrl =
                    "https://image.tmdb.org/t/p/w1280/5s7fX4VhT0X9yQn5XQW6J7VY9bC.jpg"
            });
        }
    }
}
