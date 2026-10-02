using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
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
        // My List remains architecture-only (Slice 17). Continue
        // Watching is now real as of Slice 16C - see LoadContinueWatching
        // below - populated from WatchStateService's incomplete entries,
        // never a separately-persisted collection of its own (Slice 16C
        // spec, Section 3: "Watch State -> incomplete entries -> Continue
        // Watching -> Home UI. Only Watch State should be the source of
        // truth."). HasContinueWatching/HasMyList exist purely so
        // StremioVideoCenterView.xaml can bind each MediaSection's
        // Visibility and collapse it entirely while empty, satisfying
        // "no empty carousel, no placeholder cards, no reserved vertical
        // space" without any view-side polling - the CollectionChanged
        // subscriptions below keep these two flags accurate.

        public ObservableCollection<MediaItem> ContinueWatching { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> MyList { get; }
            = new ObservableCollection<MediaItem>();

        // Slice 17C: personalised "RECOMMENDED FOR YOU" row. Filled/cleared
        // by StremioVideoCenterView from PersonalisationService results for
        // the ACTIVE profile only; hidden (HasRecommended = false) whenever
        // it is empty, same convention as MyList/ContinueWatching.
        public ObservableCollection<MediaItem> Recommended { get; }
            = new ObservableCollection<MediaItem>();

        private bool _hasRecommended;

        public bool HasRecommended
        {
            get { return _hasRecommended; }

            private set
            {
                if (_hasRecommended == value)
                {
                    return;
                }

                _hasRecommended = value;
                OnPropertyChanged(nameof(HasRecommended));
            }
        }

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
            Recommended.CollectionChanged += Recommended_CollectionChanged;

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

        private void Recommended_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            HasRecommended = Recommended.Count > 0;
        }

        // Slice 17C: replaces the row's contents (UI thread only). The
        // MediaItem instances may be shared with the catalogue cache, so
        // callers must never mutate them.
        public void SetRecommended(IEnumerable<MediaItem> items)
        {
            Recommended.Clear();

            if (items == null)
            {
                return;
            }

            foreach (MediaItem item in items)
            {
                if (item != null)
                {
                    Recommended.Add(item);
                }
            }
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
        // one row failing (a transient Cinemeta/Kitsu error) never blanks
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
        // LOAD CONTINUE WATCHING (Slice 16C)
        // =========================================================
        //
        // Continue Watching is derived entirely from Watch State's
        // incomplete entries - "Only Watch State should be the source
        // of truth" (Slice 16C spec, Section 3). No separate persisted
        // collection is ever populated here; this simply reconstructs
        // display MediaItems from WatchStateEntry each time it is
        // called (app startup and every profile switch, via
        // StremioVideoCenterView.OnActiveProfileChanged).
        //
        // Each reconstructed MediaItem carries Season/Episode exactly
        // as the WatchStateEntry does (null for a movie, populated for
        // TV/Kitsu) - HandleMediaSelectedAsync already treats an item
        // with Season/Episode already set as fully resolved and routes
        // straight to playback, so selecting a Continue Watching card
        // requires no new routing logic. A TV/Kitsu entry's StremioId
        // is the show-level id (ShowStremioId) with the episode number
        // carried separately, matching how EpisodeBrowserView already
        // builds episode MediaItems - never a next-episode inference,
        // always the exact episode last played.
        public void LoadContinueWatching(WatchStateService watchStateService, Guid profileId)
        {
            ContinueWatching.Clear();

            if (watchStateService == null)
            {
                return;
            }

            // Display/derivation limit only (Slice 16 Home presentation
            // rule) - mirrors the 25-item convention every other Home row
            // already uses via CatalogueService.DefaultTake, which Continue
            // Watching otherwise bypasses entirely since it is derived from
            // Watch State rather than fetched through CatalogueService. This
            // never touches the underlying Watch State entries themselves -
            // WatchStateService.MaxEntriesPerProfile (500, completed and
            // incomplete combined) remains the only persistence-level cap.
            // A rolling "latest 25" window falls out naturally: replaying an
            // older incomplete entry updates its LastPlayedUtc, which moves
            // it back toward the front of this ordering on the very next
            // LoadContinueWatching call.
            List<WatchStateEntry> incompleteEntries = watchStateService
                .GetEntriesForProfile(profileId)
                .Where(entry => entry != null && !entry.IsCompleted)
                .OrderByDescending(entry => entry.LastPlayedUtc)
                .Take(25)
                .ToList();

            foreach (WatchStateEntry entry in incompleteEntries)
            {
                var mediaItem = new MediaItem
                {
                    // Movie: ShowStremioId is null, CanonicalKey already
                    // is the plain "tt..." id. TV/Kitsu: ShowStremioId is
                    // the show-level id, CanonicalKey is the fuller
                    // episode-qualified identity - see WatchStateEntry
                    // remarks.
                    StremioId = entry.ShowStremioId ?? entry.CanonicalKey,
                    Title = entry.Title,
                    PosterPath = entry.PosterPath,
                    Year = entry.Year,
                    Type = entry.Type,
                    Season = entry.SeasonNumber,
                    Episode = entry.EpisodeNumber,
                    // Slice 18B: shared calculation (safe for missing
                    // duration, clamped to 100%) - Watch State stays the
                    // only source of truth.
                    Progress = PlaybackProgress.ForEntry(entry),

                    // Selecting this card resumes this exact entry via
                    // the shared resume pipeline.
                    ResumeCanonicalKey = entry.CanonicalKey
                };

                ContinueWatching.Add(mediaItem);
            }
        }


        // =========================================================
        // LOAD MY LIST (Slice 17B)
        // =========================================================
        //
        // My List is derived entirely from MyListService's stored
        // entries for the active profile - mirrors LoadContinueWatching
        // above exactly (same "reconstruct display MediaItems each call"
        // shape), called at app startup and on every profile switch via
        // StremioVideoCenterView.OnActiveProfileChanged/RefreshMyList.
        // No separate collection is persisted here - MyListService
        // remains the single source of truth (same discipline as Watch
        // State for Continue Watching).
        //
        // Each reconstructed MediaItem's StremioId is the entry's
        // CanonicalKey (always the show/movie-level identity - never
        // episode-qualified, per MyListEntry's own contract), so
        // selecting a My List card on Home routes through
        // HandleMediaSelectedAsync exactly like any other catalogue
        // card: Movie -> existing movie flow, TV/Anime -> Episode
        // Browser.
        public void LoadMyList(MyListService myListService, Guid profileId)
        {
            MyList.Clear();

            if (myListService == null)
            {
                return;
            }

            // Display limit only, same 25-item convention every other
            // Home row already uses (CatalogueService.DefaultTake /
            // LoadContinueWatching above) - never touches the underlying
            // stored My List entries themselves. GetEntriesForProfile
            // already orders most-recently-added first.
            IEnumerable<MyListEntry> entries = myListService
                .GetEntriesForProfile(profileId)
                .Take(25);

            foreach (MyListEntry entry in entries)
            {
                var mediaItem = new MediaItem
                {
                    StremioId = entry.CanonicalKey,
                    Title = entry.Title,
                    PosterPath = entry.PosterPath,
                    Year = entry.Year,
                    Type = entry.Type,
                    Genres = entry.Genres != null
                        ? new List<string>(entry.Genres)
                        : new List<string>()
                };

                MyList.Add(mediaItem);
            }
        }


        // =========================================================
        // HERO FALLBACK DATA
        // =========================================================
        //
        // HeroRecommendationService is Cinemeta (Movie/TV) + Kitsu (Anime)
        // sourced. This fixed, static fallback is shown until that loads,
        // or if it fails entirely. The slides are text-only: the image
        // URLs are intentionally empty (HeroItem.LoadBackdropAsync and the
        // Hero bindings already tolerate an empty URL), so no third-party
        // image host is referenced. No catalogue/Continue-Watching/My-List
        // dummy data is seeded here (see class remarks).

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
                    string.Empty,
                BackdropUrl =
                    string.Empty
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
                    string.Empty,
                BackdropUrl =
                    string.Empty
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
                    string.Empty,
                BackdropUrl =
                    string.Empty
            });
        }
    }
}
