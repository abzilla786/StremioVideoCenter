using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;

namespace StremioVideoCenter.ViewModels
{
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

                // =================================================
                // NOTIFY CURRENT INDEX CHANGE
                // =================================================

                OnPropertyChanged(
                    nameof(CurrentHeroIndex));


                // =================================================
                // NOTIFY CURRENT HERO CHANGE
                // =================================================
                //
                // CurrentHeroItem is calculated from
                // CurrentHeroIndex, so WPF must be told that
                // CurrentHeroItem has changed as well.
                // =================================================

                OnPropertyChanged(
                    nameof(CurrentHeroItem));
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

                if (CurrentHeroIndex < 0 ||
                    CurrentHeroIndex >= HeroItems.Count)
                {
                    return HeroItems[0];
                }

                return HeroItems[CurrentHeroIndex];
            }
        }


        // =========================================================
        // MEDIA SECTIONS
        // =========================================================

        public ObservableCollection<MediaItem> ContinueWatching { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> MyList { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> RecentlyAdded { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> TopMovies { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> TopShows { get; }
            = new ObservableCollection<MediaItem>();

        public ObservableCollection<MediaItem> TopAnime { get; }
            = new ObservableCollection<MediaItem>();


        // =========================================================
        // PROPERTY CHANGED EVENT
        // =========================================================

        public event PropertyChangedEventHandler PropertyChanged;


        // =========================================================
        // PROPERTY CHANGE HELPER
        // =========================================================

        protected virtual void OnPropertyChanged(
            string propertyName)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(
                    propertyName));
        }


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public HomeViewModel()
        {
            LoadTestData();
        }


        // =========================================================
        // LOAD HERO RECOMMENDATIONS
        // =========================================================

        public async Task LoadHeroRecommendationsAsync(
            HeroRecommendationService recommendationService)
        {
            if (recommendationService == null)
            {
                return;
            }

            try
            {
                var recommendations =
                    await recommendationService
                        .GetRecommendationsAsync();


                // =================================================
                // REQUIRE A VALID RESULT
                // =================================================

                if (recommendations == null ||
                    recommendations.Count == 0)
                {
                    return;
                }


                // =================================================
                // ONLY REPLACE THE HERO COLLECTION AFTER A
                // SUCCESSFUL RESULT
                // =================================================

                HeroItems.Clear();

                foreach (HeroItem heroItem in recommendations)
                {
                    HeroItems.Add(heroItem);
                }


                // =================================================
                // START FROM THE FIRST HERO
                // =================================================

                CurrentHeroIndex = 0;


                // =================================================
                // NOTIFY THAT THE CURRENT HERO COLLECTION HAS
                // BEEN UPDATED
                // =================================================

                OnPropertyChanged(
                    nameof(CurrentHeroItem));
            }
            catch
            {
                // =================================================
                // KEEP EXISTING HERO DATA IF TMDB FAILS
                // =================================================
                //
                // The existing test HeroItems remain available.
                // =================================================
            }
        }


        // =========================================================
        // TEST / FALLBACK DATA
        // =========================================================

        private void LoadTestData()
        {
            // =========================================================
            // HERO CAROUSEL FALLBACK
            // =========================================================

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


            // =========================================================
            // CONTINUE WATCHING
            // =========================================================

            ContinueWatching.Add(new MediaItem
            {
                Title = "Demon Slayer",
                Subtitle = "Season 4 • Episode 5",
                Year = "2024",
                Rating = "8.7",
                Type = "Anime",
                Progress = 0.65,

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/xUfRZu2mi8jH6SzQEJGP6tjBuYj.jpg"
            });


            ContinueWatching.Add(new MediaItem
            {
                Title = "The Last of Us",
                Subtitle = "Season 2 • Episode 3",
                Year = "2025",
                Rating = "8.8",
                Type = "TV",
                Progress = 0.42,

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/uKvVjHNqB5VmOrdxqAt2F7J78ED.jpg"
            });


            // =========================================================
            // MY LIST
            // =========================================================

            MyList.Add(new MediaItem
            {
                Title = "Interstellar",
                Year = "2014",
                Rating = "8.7",
                Type = "Movie",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/gEU2QniE6E77NI6lCU6MxlNBvIx.jpg"
            });


            MyList.Add(new MediaItem
            {
                Title = "One Piece",
                Year = "1999",
                Rating = "9.0",
                Type = "Anime",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/fcXdJlbSdUEeMSJFsXKsznGwwok.jpg"
            });


            // =========================================================
            // RECENTLY ADDED
            // =========================================================

            RecentlyAdded.Add(new MediaItem
            {
                Title = "Big Buck Bunny",
                Year = "2010",
                Rating = "7.5",
                Type = "Movie",

                StremioId = "tt1254207",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/8cdWjvZQUExUUTzyp4t6EDMubfO.jpg"
            });


            // =========================================================
            // TOP MOVIES
            // =========================================================

            TopMovies.Add(new MediaItem
            {
                Title = "Interstellar",
                Year = "2014",
                Rating = "8.7",
                Type = "Movie",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/gEU2QniE6E77NI6lCU6MxlNBvIx.jpg"
            });


            TopMovies.Add(new MediaItem
            {
                Title = "Inception",
                Year = "2010",
                Rating = "8.8",
                Type = "Movie",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/9gk7adHYeDvHkCSEqAvQNLV5Uge.jpg"
            });


            // =========================================================
            // TOP TV
            // =========================================================

            TopShows.Add(new MediaItem
            {
                Title = "The Last of Us",
                Year = "2025",
                Rating = "8.8",
                Type = "TV",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/uKvVjHNqB5VmOrdxqAt2F7J78ED.jpg"
            });


            // =========================================================
            // TOP ANIME
            // =========================================================

            TopAnime.Add(new MediaItem
            {
                Title = "Demon Slayer",
                Year = "2024",
                Rating = "8.7",
                Type = "Anime",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/xUfRZu2mi8jH6SzQEJGP6tjBuYj.jpg"
            });


            TopAnime.Add(new MediaItem
            {
                Title = "One Piece",
                Year = "1999",
                Rating = "9.0",
                Type = "Anime",

                PosterPath =
                    "https://image.tmdb.org/t/p/w342/fcXdJlbSdUEeMSJFsXKsznGwwok.jpg"
            });
        }
    }
}