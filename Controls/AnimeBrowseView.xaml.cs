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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // ANIME BROWSE VIEW (Slice 15 - dedicated Anime page)
    // =============================================================
    //
    // Two independent content areas, both real and both honestly
    // labelled - never merged into one fabricated "catalogue":
    //
    //   - FEATURED / POPULAR / NEW ANIME: the same TMDB-backed rows
    //     Home already shows (CatalogueService.GetFeaturedAnimeAsync/
    //     GetPopularAnimeAsync/GetNewAnimeAsync - unchanged logic).
    //     Honestly empty when no TmdbService is configured.
    //   - BROWSE ANIMEFLV: a genre-or-search browser against
    //     AnimeFlvCatalogueService's own two real, live-verified
    //     catalogs (see that class's remarks). AnimeFLV has no
    //     unfiltered/ranked catalog at all - every one of its 13 real
    //     catalogs requires either "search" or "genre" - so this is
    //     deliberately a browser, not another "Featured"/"Popular" row.
    //
    // Same overlay conventions as CatalogueBrowseView/EpisodeBrowserView/
    // SourceSelectorView - Closed/MediaSelected events bridged into an
    // awaitable Task by the caller, PreviewKeyDown for keyboard,
    // HandleControllerInput(ControllerInput) for the shared "most
    // specific overlay wins" input chain.
    public partial class AnimeBrowseView : UserControl
    {
        // MediaCard's own fixed Border Width (190) plus its Margin (8 on
        // every side = 16 total) - see MediaCard.xaml / CatalogueBrowseView's
        // identical remark on FilteredCardWidth.
        private const double CardWidth = 206;

        private const int AnimeFlvPageSize = 25;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        // Persistent hero navigation task: same quick-nav row event as
        // CatalogueBrowseView's own NavigationRequested - see that file's
        // remarks.
        public event EventHandler<string> NavigationRequested;

        private CatalogueService _catalogueService;
        private AnimeFlvCatalogueService _animeFlvCatalogueService;
        private KitsuService _kitsuService;

        // Persistent hero navigation task: same header-row Left/Right
        // source-of-truth convention as CatalogueBrowseView's own
        // _headerNavButtons.
        private Button[] _headerNavButtons;

        private readonly ObservableCollection<MediaItem> _featuredItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _popularItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _newItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _kitsuPopularItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _animeFlvItems = new ObservableCollection<MediaItem>();

        private bool _suppressGenreEvents;

        // Search mode wins over genre mode whenever a search has been
        // explicitly submitted - null means "genre-browse mode" (the
        // default, since AnimeFLV requires one or the other and genre
        // needs no typed input from the user).
        private string _activeSearchQuery;
        private string _activeGenre;

        private int _animeFlvSkip;
        private int _animeFlvLoadToken;
        private bool _isLoadingAnimeFlv;

        public AnimeBrowseView()
        {
            InitializeComponent();

            // Catches the bubbling MediaCard.SelectedEvent from the raw
            // MediaCards inside AnimeFlvItemsControl - see
            // CatalogueBrowseView's identical remark on
            // FilteredMediaCard_Selected for why this is needed (the
            // WrapPanel grid isn't wrapped in a MediaSection, so nothing
            // else already consumes the event on the way up).
            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(AnimeFlvMediaCard_Selected));

            FeaturedAnimeSection.MediaSelected += Section_MediaSelected;
            PopularAnimeSection.MediaSelected += Section_MediaSelected;
            NewAnimeSection.MediaSelected += Section_MediaSelected;
            KitsuPopularSection.MediaSelected += Section_MediaSelected;

            _headerNavButtons =
                new[]
                {
                    QuickNavHomeButton,
                    QuickNavMoviesButton,
                    QuickNavTvShowsButton,
                    QuickNavAnimeButton,
                    CloseButton
                };
        }

        // =========================================================
        // INITIALIZE
        // =========================================================

        public void Initialize(CatalogueService catalogueService, AnimeFlvCatalogueService animeFlvCatalogueService, KitsuService kitsuService)
        {
            if (catalogueService == null)
            {
                throw new ArgumentNullException(nameof(catalogueService));
            }

            if (animeFlvCatalogueService == null)
            {
                throw new ArgumentNullException(nameof(animeFlvCatalogueService));
            }

            if (kitsuService == null)
            {
                throw new ArgumentNullException(nameof(kitsuService));
            }

            _catalogueService = catalogueService;
            _animeFlvCatalogueService = animeFlvCatalogueService;
            _kitsuService = kitsuService;

            FeaturedAnimeSection.Items = _featuredItems;
            PopularAnimeSection.Items = _popularItems;
            NewAnimeSection.Items = _newItems;
            KitsuPopularSection.Items = _kitsuPopularItems;
            AnimeFlvItemsControl.ItemsSource = _animeFlvItems;

            PopulateGenreCombo();

            // All three areas load independently and concurrently - none
            // waits on another, matching Home's own fail-soft,
            // independent-row loading approach.
            BeginLoadTmdbRows();
            BeginLoadKitsuPopular();
            BeginLoadAnimeFlv();
        }

        // Initialize stays synchronous (matching the un-awaited
        // "page.Initialize(...)" call site in
        // StremioVideoCenterView.ShowAnimePageAsync) - these async-void
        // shims are the same fire-and-forget-from-a-sync-context pattern
        // already used by MediaCard's own Loaded handler.
        private async void BeginLoadTmdbRows()
        {
            await LoadTmdbRowsAsync();
        }

        private async void BeginLoadAnimeFlv()
        {
            await LoadAnimeFlvPageAsync(focusFirstResult: false);
        }

        private async void BeginLoadKitsuPopular()
        {
            await LoadKitsuPopularAsync();
        }

        // =========================================================
        // KITSU-BACKED POPULAR ROW (Slice 15 Kitsu Migration)
        // =========================================================
        //
        // Independent of both the TMDB rows and the AnimeFLV browse grid
        // below - a third, separate data source, never blended with
        // either. Fail-soft: an unreachable Kitsu just leaves this row
        // empty (KitsuEmptyText), exactly like the TMDB rows' own
        // TmdbEmptyText behaviour when TMDB isn't configured.
        private async Task LoadKitsuPopularAsync()
        {
            List<KitsuAnimeResource> results;

            try
            {
                results = await _kitsuService.GetPopularAnimeAsync();
            }
            catch
            {
                results = new List<KitsuAnimeResource>();
            }

            List<MediaItem> mapped = (results ?? new List<KitsuAnimeResource>())
                .Select(MapKitsuToMediaItem)
                .Where(item => item != null)
                .ToList();

            ReplaceCollection(_kitsuPopularItems, mapped);

            KitsuPopularSection.Visibility = mapped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            KitsuEmptyText.Visibility = mapped.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // Maps a KitsuAnimeResource into the same MediaItem shape every
        // other catalogue source in this project already produces.
        // StremioId is "kitsu:<id>" - the real, documented Kitsu/Stremio
        // id convention (see KitsuService's own remarks) - NOT the raw
        // numeric Kitsu id alone, so ContentRequest/PlaySelectedMediaAsync
        // can recognise it by prefix alongside the existing "tt"-prefixed
        // convention without any additional per-item flag.
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
                Title = title,
                Overview = item.Attributes.Synopsis,
                ReleaseDate = item.Attributes.StartDate,
                Rating = item.Attributes.AverageRating,
                Type = "Anime",
                Genres = new List<string>(),
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
        // TMDB-BACKED FEATURED / POPULAR / NEW ANIME
        // =========================================================

        private async Task LoadTmdbRowsAsync()
        {
            Task<List<MediaItem>> featuredTask = SafeFetchAsync(() => _catalogueService.GetFeaturedAnimeAsync());
            Task<List<MediaItem>> popularTask = SafeFetchAsync(() => _catalogueService.GetPopularAnimeAsync());
            Task<List<MediaItem>> newTask = SafeFetchAsync(() => _catalogueService.GetNewAnimeAsync());

            await Task.WhenAll(featuredTask, popularTask, newTask);

            List<MediaItem> featured = featuredTask.Result;
            List<MediaItem> popular = popularTask.Result;
            List<MediaItem> newest = newTask.Result;

            ReplaceCollection(_featuredItems, featured);
            ReplaceCollection(_popularItems, popular);
            ReplaceCollection(_newItems, newest);

            FeaturedAnimeSection.Visibility = featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PopularAnimeSection.Visibility = popular.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NewAnimeSection.Visibility = newest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool anyContent = featured.Count > 0 || popular.Count > 0 || newest.Count > 0;
            TmdbEmptyText.Visibility = anyContent ? Visibility.Collapsed : Visibility.Visible;
        }

        private static async Task<List<MediaItem>> SafeFetchAsync(Func<Task<List<MediaItem>>> fetch)
        {
            try
            {
                List<MediaItem> result = await fetch();
                return result ?? new List<MediaItem>();
            }
            catch
            {
                return new List<MediaItem>();
            }
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
        // ANIMEFLV GENRE COMBO
        // =========================================================

        private void PopulateGenreCombo()
        {
            _suppressGenreEvents = true;

            GenreCombo.Items.Clear();

            foreach (string genre in AnimeFlvCatalogueService.GenreOptions)
            {
                GenreCombo.Items.Add(genre);
            }

            // AnimeFLV's "genre" extra is REQUIRED on this catalog (see
            // AnimeFlvCatalogueService remarks) - there is no "Any"/
            // unfiltered option to default to, so the first verified
            // genre is selected and loaded automatically instead of
            // leaving the browser empty until the user picks one.
            GenreCombo.SelectedIndex = 0;

            _activeGenre = GenreCombo.SelectedItem as string;

            _suppressGenreEvents = false;
        }

        private async void GenreCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressGenreEvents)
            {
                return;
            }

            string genre = GenreCombo.SelectedItem as string;

            if (string.IsNullOrEmpty(genre))
            {
                return;
            }

            // Picking a genre switches back to genre-browse mode even if
            // a search was active - the search box is cleared so the UI
            // never implies both are active at once (AnimeFLV can only
            // ever answer one or the other per request).
            _activeGenre = genre;
            _activeSearchQuery = null;
            SearchBox.Text = string.Empty;

            await RestartAnimeFlvAsync();
        }

        // =========================================================
        // ANIMEFLV SEARCH
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

            _activeSearchQuery = query;

            await RestartAnimeFlvAsync();
        }

        // =========================================================
        // ANIMEFLV FETCH / PAGINATION
        // =========================================================

        private async Task RestartAnimeFlvAsync()
        {
            _animeFlvSkip = 0;
            _animeFlvItems.Clear();
            AnimeFlvEmptyText.Visibility = Visibility.Collapsed;
            AnimeFlvLoadMoreButton.Visibility = Visibility.Collapsed;

            await LoadAnimeFlvPageAsync(focusFirstResult: true);
        }

        private async Task LoadAnimeFlvPageAsync(bool focusFirstResult)
        {
            _isLoadingAnimeFlv = true;
            AnimeFlvLoadMoreButton.IsEnabled = false;

            int requestSkip = _animeFlvSkip;
            int loadToken = ++_animeFlvLoadToken;

            if (requestSkip == 0)
            {
                AnimeFlvLoadingText.Visibility = Visibility.Visible;
            }

            List<AnimeFlvCatalogItem> page;

            try
            {
                page = !string.IsNullOrEmpty(_activeSearchQuery)
                    ? await _animeFlvCatalogueService.GetSearchCatalogAsync(_activeSearchQuery, requestSkip)
                    : await _animeFlvCatalogueService.GetGenreCatalogAsync(_activeGenre, requestSkip);
            }
            catch
            {
                page = new List<AnimeFlvCatalogItem>();
            }

            if (loadToken != _animeFlvLoadToken)
            {
                // A newer genre/search selection superseded this request
                // while it was still in flight - discard rather than mix
                // stale results in with the new selection's.
                return;
            }

            AnimeFlvLoadingText.Visibility = Visibility.Collapsed;

            List<MediaItem> mapped = page
                .Select(MapToMediaItem)
                .Where(item => item != null)
                .ToList();

            foreach (MediaItem item in mapped)
            {
                _animeFlvItems.Add(item);
            }

            _animeFlvSkip = requestSkip + page.Count;

            bool hasAny = _animeFlvItems.Count > 0;
            AnimeFlvEmptyText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

            // A short page is treated as "no more results" - the addon's
            // own catalog protocol exposes no total-count field to track
            // against instead (same reasoning as CatalogueBrowseView's
            // identical Cinemeta pagination).
            AnimeFlvLoadMoreButton.Visibility =
                (hasAny && page.Count >= AnimeFlvPageSize) ? Visibility.Visible : Visibility.Collapsed;
            AnimeFlvLoadMoreButton.IsEnabled = true;

            _isLoadingAnimeFlv = false;

            if (focusFirstResult && hasAny)
            {
                Dispatcher.BeginInvoke(new Action(() => FocusAnimeFlvCard(0)), DispatcherPriority.Input);
            }
        }

        private async void AnimeFlvLoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoadingAnimeFlv)
            {
                return;
            }

            await LoadAnimeFlvPageAsync(focusFirstResult: false);
        }

        // Maps AnimeFlvCatalogItem (the addon's own Stremio "meta preview
        // object" shape - see AnimeFlvCatalogueService remarks) into the
        // same MediaItem shape every other catalogue source in this
        // project already produces. StremioId is the item's own native
        // id used verbatim (may be a non-"tt" id, e.g. "animeflv:...") -
        // PlaySelectedMediaAsync already sends a show-level MediaItem's
        // StremioId as ContentRequest.ExplicitStremioId unconditionally,
        // so this requires no further playback-pipeline changes (see
        // AnimeFlvCatalogueService's own class remarks).
        private static MediaItem MapToMediaItem(AnimeFlvCatalogItem item)
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

        private void AnimeFlvMediaCard_Selected(object sender, RoutedEventArgs e)
        {
            MediaCard card = e.OriginalSource as MediaCard;

            if (card == null)
            {
                return;
            }

            MediaItem item = card.DataContext as MediaItem;

            if (item == null)
            {
                return;
            }

            RaiseMediaSelected(item);

            e.Handled = true;
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

        // Persistent hero navigation task: same quick-nav handlers as
        // CatalogueBrowseView's own - see that file's remarks.
        private void QuickNavHomeButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void QuickNavMoviesButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseNavigationRequested("Movies");
        }

        private void QuickNavTvShowsButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseNavigationRequested("TvShows");
        }

        private void QuickNavAnimeButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseNavigationRequested("Anime");
        }

        private void RaiseNavigationRequested(string destination)
        {
            EventHandler<string> handler = NavigationRequested;

            if (handler != null)
            {
                handler(this, destination);
            }

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

        private void AnimeBrowseView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // The search box needs its own text-entry keys (including
            // Left/Right/Space) to reach the TextBox normally - only
            // Escape is intercepted globally regardless of focus.
            if (e.Key == Key.Escape)
            {
                RaiseClosed();
                e.Handled = true;
                return;
            }

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
                    return ActivateFocusedElement();
            }

            return false;
        }

        // Dedicated-page Back navigation task: lets controller A activate
        // the Back button when it has focus - see CatalogueBrowseView's
        // identical remark on its own ActivateFocusedElement for why this
        // page's own HandleControllerInput (not the generic keyboard
        // Enter/Space path) needs this.
        private bool ActivateFocusedElement()
        {
            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return ActivateFocusedCard();
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
        // FOCUS MOVEMENT
        // =========================================================
        //
        // Same shape as CatalogueBrowseView's identical section: three
        // stacked MediaSections use their own FocusFirstCard/
        // MoveHorizontal, the AnimeFLV WrapPanel grid uses an estimated
        // columns-per-row (see CardWidth's remarks). Both areas are
        // always visible at once here (unlike CatalogueBrowseView's
        // mutually-exclusive default/filtered views), so vertical
        // movement simply tries the TMDB sections first, then falls
        // through to the AnimeFLV grid.

        // Despite the name (kept for minimal diff against the rest of
        // this file's existing remarks/method chain), this now also
        // includes KitsuPopularSection (Slice 15 Kitsu Migration) - the
        // stacked-MediaSection chain above the AnimeFLV browse grid,
        // regardless of which provider backs each row.
        private MediaSection[] GetVisibleTmdbSections()
        {
            return new[] { FeaturedAnimeSection, PopularAnimeSection, NewAnimeSection, KitsuPopularSection }
                .Where(section => section.Visibility == Visibility.Visible)
                .ToArray();
        }

        private MediaSection GetFocusedTmdbSection()
        {
            foreach (MediaSection section in GetVisibleTmdbSections())
            {
                if (section.IsKeyboardFocusWithin)
                {
                    return section;
                }
            }

            return null;
        }

        private bool IsFocusInAnimeFlvGrid()
        {
            return GetFocusedAnimeFlvCardIndex() >= 0;
        }

        private void MoveVertical(int direction)
        {
            // Dedicated-page Back navigation task, extended by the
            // persistent hero navigation task: same top-layer <-> content
            // chaining as CatalogueBrowseView/SearchView - see those
            // files' identical remark on their own MoveVertical. The
            // "top layer" is now the whole header row (quick-nav buttons
            // + Back), not just Back alone.
            if (IsHeaderRowFocused())
            {
                if (direction > 0)
                {
                    FocusFirstAvailable();
                }

                return;
            }

            if (IsFocusInAnimeFlvGrid())
            {
                if (direction < 0 && GetFocusedAnimeFlvCardIndex() < GetAnimeFlvColumnsPerRow())
                {
                    // Moving up out of the grid's first row - land back on
                    // the last visible TMDB section, if there is one, or
                    // on the Back button if there isn't.
                    MediaSection[] sections = GetVisibleTmdbSections();

                    if (sections.Length > 0)
                    {
                        sections[sections.Length - 1].FocusFirstCard();
                        return;
                    }

                    CloseButton.Focus();
                    return;
                }

                MoveAnimeFlvGrid(0, direction);
                return;
            }

            MediaSection[] visibleSections = GetVisibleTmdbSections();
            MediaSection current = GetFocusedTmdbSection();
            int index = Array.IndexOf(visibleSections, current);

            if (index < 0)
            {
                FocusFirstAvailable();
                return;
            }

            int target = index + direction;

            if (target < 0)
            {
                // Dedicated-page Back navigation task: moving up out of
                // the topmost TMDB section reaches the Back button
                // instead of clamping in place.
                CloseButton.Focus();
                return;
            }

            if (target >= visibleSections.Length)
            {
                // Moving down past the last TMDB section - hand off to
                // the AnimeFLV grid, which is always beneath them.
                FocusAnimeFlvCard(0);
                return;
            }

            visibleSections[target].FocusFirstCard();
        }

        // Dedicated-page Back navigation task: Down from the Back button
        // needs to reach whichever content actually comes first - a
        // visible TMDB section, or the AnimeFLV grid if none are visible
        // (same fallback order the "no focus yet" branch above uses).
        private void FocusFirstAvailable()
        {
            MediaSection[] sections = GetVisibleTmdbSections();

            if (sections.Length > 0)
            {
                sections[0].FocusFirstCard();
            }
            else
            {
                FocusAnimeFlvCard(0);
            }
        }

        private void MoveHorizontal(int direction)
        {
            // Persistent hero navigation task: checked first, same as
            // CatalogueBrowseView's own MoveHorizontal - Left/Right while
            // any header-row button has focus cycles the quick-nav
            // buttons and Back, clamping at either end.
            if (IsHeaderRowFocused())
            {
                MoveHeaderRowHorizontal(direction);
                return;
            }

            if (IsFocusInAnimeFlvGrid())
            {
                MoveAnimeFlvGrid(direction, 0);
                return;
            }

            GetFocusedTmdbSection()?.MoveHorizontal(direction);
        }

        private bool IsHeaderRowFocused()
        {
            return Array.Exists(
                _headerNavButtons,
                button => button != null && button.IsKeyboardFocused);
        }

        private void MoveHeaderRowHorizontal(int direction)
        {
            int currentIndex =
                Array.FindIndex(
                    _headerNavButtons,
                    button => button != null && button.IsKeyboardFocused);

            if (currentIndex < 0)
            {
                return;
            }

            int target =
                Math.Max(
                    0,
                    Math.Min(currentIndex + direction, _headerNavButtons.Length - 1));

            _headerNavButtons[target]?.Focus();
        }

        // =========================================================
        // ANIMEFLV GRID FOCUS MOVEMENT
        // =========================================================

        private int GetAnimeFlvColumnsPerRow()
        {
            double width = AnimeFlvItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            int columns = (int)(width / CardWidth);

            return Math.Max(1, columns);
        }

        private void MoveAnimeFlvGrid(int deltaColumns, int deltaRows)
        {
            int count = AnimeFlvItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedAnimeFlvCardIndex();

            if (current < 0)
            {
                FocusAnimeFlvCard(0);
                return;
            }

            int columns = GetAnimeFlvColumnsPerRow();
            int target = current + deltaColumns + (deltaRows * columns);

            target = Math.Max(0, Math.Min(target, count - 1));

            FocusAnimeFlvCard(target);
        }

        private int GetFocusedAnimeFlvCardIndex()
        {
            for (int i = 0; i < AnimeFlvItemsControl.Items.Count; i++)
            {
                ContentPresenter presenter =
                    AnimeFlvItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

                if (presenter == null)
                {
                    continue;
                }

                MediaCard card = FindVisualChild<MediaCard>(presenter);

                if (card != null && card.IsKeyboardFocusWithin)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusAnimeFlvCard(int index)
        {
            if (index < 0 || index >= AnimeFlvItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                AnimeFlvItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusAnimeFlvCard(index)),
                    DispatcherPriority.Loaded);

                return;
            }

            MediaCard card = FindVisualChild<MediaCard>(presenter);

            if (card == null)
            {
                return;
            }

            card.Focus();
            card.BringIntoView();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                T result = child as T;

                if (result != null)
                {
                    return result;
                }

                result = FindVisualChild<T>(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
