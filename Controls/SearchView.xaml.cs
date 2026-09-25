using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // SEARCH VIEW (Slice 15 - global search)
    // =============================================================
    //
    // Searches across Movies, TV Shows, and Anime using only verified,
    // real provider search capabilities - no custom scraper and no
    // second/duplicate search engine of this project's own:
    //
    //   - Cinemeta's own "search" extra on movie/top - live-verified
    //     this slice, ranked and functional.
    //   - Cinemeta's own "search" extra on series/top - independently
    //     live-verified this slice (previously only assumed "by
    //     symmetry" with movie/top - see CatalogueService/Cinemeta_
    //     Capability_Audit.md).
    //   - AnimeFLV's primary "animeflv" search catalog (its own
    //     required "search" extra) - the same single-source choice
    //     AnimeFlvCatalogueService.GetSearchCatalogAsync already makes
    //     for this exact "don't fan out 6x against a slow scraping
    //     backend" reason.
    //
    // All three are queried concurrently on submit and shown as up to
    // three separate MediaSections (Movies/TV Shows/Anime), each only
    // visible when it actually has results - never a fabricated empty
    // row. No pagination on search results this slice (unlike the
    // dedicated Movies/TV/Anime pages) - kept deliberately simple,
    // matching this task's own "small, maintainable changes" guidance;
    // Cinemeta's search extra does support skip if a future slice wants
    // to add it.
    //
    // Same overlay conventions as every other overlay in this codebase -
    // Closed/MediaSelected events bridged into an awaitable Task by the
    // caller, PreviewKeyDown for keyboard, HandleControllerInput for the
    // shared "most specific overlay wins" input chain.
    public partial class SearchView : UserControl
    {
        private const int SearchResultTake = 25;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        private CinemetaService _cinemetaService;
        private AnimeFlvCatalogueService _animeFlvCatalogueService;

        private readonly ObservableCollection<MediaItem> _movieItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _tvItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _animeItems = new ObservableCollection<MediaItem>();

        // Guards against an older, slower search's results landing after
        // a newer one has already started - the same stale-response
        // guard used by CatalogueBrowseView/AnimeBrowseView's pagination.
        private int _searchToken;

        public SearchView()
        {
            InitializeComponent();

            MoviesSection.MediaSelected += Section_MediaSelected;
            TvSection.MediaSelected += Section_MediaSelected;
            AnimeSection.MediaSelected += Section_MediaSelected;
        }

        // =========================================================
        // INITIALIZE
        // =========================================================

        public void Initialize(CinemetaService cinemetaService, AnimeFlvCatalogueService animeFlvCatalogueService)
        {
            if (cinemetaService == null)
            {
                throw new ArgumentNullException(nameof(cinemetaService));
            }

            if (animeFlvCatalogueService == null)
            {
                throw new ArgumentNullException(nameof(animeFlvCatalogueService));
            }

            _cinemetaService = cinemetaService;
            _animeFlvCatalogueService = animeFlvCatalogueService;

            MoviesSection.Items = _movieItems;
            TvSection.Items = _tvItems;
            AnimeSection.Items = _animeItems;

            ShowPromptState();
        }

        public void FocusSearchBox()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }), DispatcherPriority.Input);
        }

        // =========================================================
        // SEARCH SUBMISSION
        // =========================================================

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SubmitSearchAsync();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitSearchAsync();
            }
        }

        private async Task SubmitSearchAsync()
        {
            string query = SearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            int token = ++_searchToken;

            ShowLoadingState();

            Task<List<MediaItem>> moviesTask = SafeSearchCinemetaAsync("movie", query, "Movie");
            Task<List<MediaItem>> tvTask = SafeSearchCinemetaAsync("series", query, "TV");
            Task<List<MediaItem>> animeTask = SafeSearchAnimeFlvAsync(query);

            await Task.WhenAll(moviesTask, tvTask, animeTask);

            if (token != _searchToken)
            {
                // A newer search superseded this one while it was still
                // in flight - its results are stale and are discarded.
                return;
            }

            List<MediaItem> movies = moviesTask.Result;
            List<MediaItem> tv = tvTask.Result;
            List<MediaItem> anime = animeTask.Result;

            ReplaceCollection(_movieItems, movies);
            ReplaceCollection(_tvItems, tv);
            ReplaceCollection(_animeItems, anime);

            MoviesSection.Visibility = movies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TvSection.Visibility = tv.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AnimeSection.Visibility = anime.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool anyResults = movies.Count > 0 || tv.Count > 0 || anime.Count > 0;

            if (anyResults)
            {
                ShowResultsState();

                Dispatcher.BeginInvoke(new Action(FocusFirstAvailableSection), DispatcherPriority.Input);
            }
            else
            {
                ShowEmptyState();
            }
        }

        private async Task<List<MediaItem>> SafeSearchCinemetaAsync(string cinemetaType, string query, string mediaItemType)
        {
            try
            {
                string extraSegment = "search=" + Uri.EscapeDataString(query);

                List<CinemetaCatalogItem> items =
                    await _cinemetaService.GetCatalogAsync(cinemetaType, "top", extraSegment);

                return MapCinemetaItems(items, mediaItemType);
            }
            catch
            {
                return new List<MediaItem>();
            }
        }

        private async Task<List<MediaItem>> SafeSearchAnimeFlvAsync(string query)
        {
            try
            {
                List<AnimeFlvCatalogItem> items = await _animeFlvCatalogueService.GetSearchCatalogAsync(query);

                return items
                    .Select(MapAnimeFlvItem)
                    .Where(item => item != null)
                    .Take(SearchResultTake)
                    .ToList();
            }
            catch
            {
                return new List<MediaItem>();
            }
        }

        // Mirrors CatalogueService.BuildMediaItemsFromCinemeta's own
        // field mapping (a real, already-established shape - not a new
        // one invented for search) - duplicated rather than shared since
        // that method is private to CatalogueService and this is a
        // different, search-specific caller with its own "type" per
        // Cinemeta catalog queried (movie vs. series).
        private static List<MediaItem> MapCinemetaItems(List<CinemetaCatalogItem> items, string mediaItemType)
        {
            var result = new List<MediaItem>();

            if (items == null)
            {
                return result;
            }

            foreach (CinemetaCatalogItem item in items)
            {
                if (result.Count >= SearchResultTake)
                {
                    break;
                }

                if (item == null)
                {
                    continue;
                }

                string imdbId = !string.IsNullOrWhiteSpace(item.ImdbId) ? item.ImdbId : item.Id;

                if (string.IsNullOrWhiteSpace(imdbId) ||
                    !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Poster))
                {
                    continue;
                }

                result.Add(new MediaItem
                {
                    StremioId = imdbId,
                    TmdbId = null,
                    Title = item.Name,
                    Overview = item.Description,
                    Year = !string.IsNullOrWhiteSpace(item.Year) ? item.Year : item.ReleaseInfo,
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

        // Same mapping AnimeBrowseView.MapToMediaItem uses - duplicated
        // per this project's existing per-view convention rather than
        // shared (see that method's own remarks on StremioId/
        // ExplicitStremioId).
        private static MediaItem MapAnimeFlvItem(AnimeFlvCatalogItem item)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                return null;
            }

            return new MediaItem
            {
                StremioId = item.Id,
                Title = item.Name,
                Overview = item.Description,
                Year = item.ReleaseInfo,
                Rating = item.ImdbRating,
                Type = "Anime",
                Genres = item.Genres ?? new List<string>(),
                PosterPath = item.Poster,
                BackdropUrl = string.Empty
            };
        }

        private static void ReplaceCollection(ObservableCollection<MediaItem> target, List<MediaItem> source)
        {
            target.Clear();

            if (source == null)
            {
                return;
            }

            foreach (MediaItem item in source)
            {
                target.Add(item);
            }
        }

        private void Section_MediaSelected(object sender, MediaItemSelectedEventArgs e)
        {
            RaiseMediaSelected(e.Item);
        }

        // =========================================================
        // VIEW STATES
        // =========================================================

        private void ShowPromptState()
        {
            PromptText.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        private void ShowLoadingState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        private void ShowResultsState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Visible;
        }

        private void ShowEmptyState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        // =========================================================
        // SELECTION / CLOSE
        // =========================================================

        private void RaiseMediaSelected(MediaItem item)
        {
            EventHandler<MediaItemSelectedEventArgs> handler = MediaSelected;

            if (handler != null)
            {
                handler(this, new MediaItemSelectedEventArgs(item));
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            EventHandler handler = Closed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void SearchView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RaiseClosed();
                e.Handled = true;
                return;
            }

            // The search box needs its own text-entry keys (including
            // Left/Right/Space/Up/Down for caret movement) to reach the
            // TextBox normally.
            if (SearchBox.IsKeyboardFocusWithin)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Up:
                    MoveVertical(-1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    MoveVertical(1);
                    e.Handled = true;
                    break;

                case Key.Left:
                    MoveHorizontal(-1);
                    e.Handled = true;
                    break;

                case Key.Right:
                    MoveHorizontal(1);
                    e.Handled = true;
                    break;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            switch (button)
            {
                case ControllerInput.B:
                    RaiseClosed();
                    return true;

                case ControllerInput.DPadUp:
                    MoveVertical(-1);
                    return true;

                case ControllerInput.DPadDown:
                    MoveVertical(1);
                    return true;

                case ControllerInput.DPadLeft:
                    MoveHorizontal(-1);
                    return true;

                case ControllerInput.DPadRight:
                    MoveHorizontal(1);
                    return true;

                case ControllerInput.A:
                    return ActivateFocusedCard();
            }

            return false;
        }

        private bool ActivateFocusedCard()
        {
            MediaCard card = FindFocusedMediaCard(this);

            if (card == null)
            {
                return false;
            }

            card.RaiseEvent(new RoutedEventArgs(MediaCard.SelectedEvent));

            return true;
        }

        private static MediaCard FindFocusedMediaCard(DependencyObject parent)
        {
            if (parent == null)
            {
                return null;
            }

            MediaCard card = parent as MediaCard;

            if (card != null && card.IsKeyboardFocusWithin)
            {
                return card;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                MediaCard result = FindFocusedMediaCard(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        // =========================================================
        // FOCUS MOVEMENT (three stacked MediaSections - same shape as
        // CatalogueBrowseView's default view)
        // =========================================================

        private MediaSection[] GetVisibleSections()
        {
            return new[] { MoviesSection, TvSection, AnimeSection }
                .Where(section => section.Visibility == Visibility.Visible)
                .ToArray();
        }

        private MediaSection GetFocusedSection()
        {
            foreach (MediaSection section in GetVisibleSections())
            {
                if (section.IsKeyboardFocusWithin)
                {
                    return section;
                }
            }

            return null;
        }

        private void MoveVertical(int direction)
        {
            MediaSection[] sections = GetVisibleSections();

            if (sections.Length == 0)
            {
                return;
            }

            MediaSection current = GetFocusedSection();
            int index = Array.IndexOf(sections, current);

            if (index < 0)
            {
                sections[0].FocusFirstCard();
                return;
            }

            int target = Math.Max(0, Math.Min(index + direction, sections.Length - 1));
            sections[target].FocusFirstCard();
        }

        private void MoveHorizontal(int direction)
        {
            GetFocusedSection()?.MoveHorizontal(direction);
        }

        private void FocusFirstAvailableSection()
        {
            MediaSection[] sections = GetVisibleSections();

            if (sections.Length > 0)
            {
                sections[0].FocusFirstCard();
            }
        }
    }
}
