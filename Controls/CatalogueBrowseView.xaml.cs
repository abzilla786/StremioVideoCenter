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
    // CATALOGUE BROWSE VIEW (Slice 15 - Movies & TV dedicated pages)
    // =============================================================
    //
    // One shared, parameterized overlay for both the dedicated Movies
    // page and the dedicated TV page (Initialize(isTv, ...) picks the
    // side) - see StremioVideoCenterView.ShowCatalogueBrowsePageAsync,
    // which already constructs a CatalogueService (Cinemeta-first,
    // TMDB-fallback, unchanged from Slice 13/CatalogueService.cs) and
    // passes it in. This view never talks to CinemetaService/TmdbService
    // directly and never fabricates data - every item shown here came
    // back from CatalogueService's own real-provider calls.
    //
    // Two mutually exclusive content states:
    //   - DEFAULT VIEW: three horizontally-scrolling MediaSections
    //     (Featured/Popular/New Releases), exactly mirroring Home's own
    //     rows but for this media type alone - shown whenever no Genre
    //     or Year filter is engaged.
    //   - FILTERED VIEW: a single WrapPanel grid of MediaCards plus a
    //     "Load More" button, using Cinemeta's skip-based pagination
    //     (CatalogueService already threads take/skip through) - shown
    //     whenever a Genre and/or Year filter is engaged.
    //
    // No rating filter is offered anywhere on this page - Cinemeta
    // declares no rating/minimum-rating extra on any catalog it exposes
    // (live-verified this slice), so a rating control would either be
    // fake client-side filtering over a partial page or a dead control.
    // FilterLimitationText documents this, and also documents the one
    // other real protocol limitation surfaced by combining filters: see
    // ApplyFiltersAsync's remarks on Year vs. Genre below.
    //
    // Same overlay conventions as every other overlay in this codebase
    // (EpisodeBrowserView/SourceSelectorView/LocalMediaView): a
    // UserControl with Closed/MediaSelected events bridged into an
    // awaitable Task by the caller, PreviewKeyDown for keyboard,
    // HandleControllerInput(ControllerInput) for the "most specific
    // overlay wins" input-priority chain, and hand-rolled focus
    // management rather than WPF's built-in directional navigation
    // (which this codebase doesn't rely on anywhere else either).
    public partial class CatalogueBrowseView : UserControl
    {
        // MediaCard's own fixed Border Width (190) plus its Margin (8 on
        // every side = 16 total) - see MediaCard.xaml. Used only to
        // estimate how many columns the FilteredItemsControl's WrapPanel
        // is currently laying out, so Up/Down in the filtered grid can
        // move by a row rather than only ever moving one item at a time.
        // Not exact geometry (WPF doesn't expose "columns in this wrap"
        // directly) but a reasonable, honest approximation given every
        // card in this grid is the same fixed width.
        private const double FilteredCardWidth = 206;

        private const int FilteredPageSize = 25;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        // Persistent hero navigation task: raised by the quick-nav row's
        // Movies/TV Shows/Anime buttons (Home reuses RaiseClosed()
        // directly instead - see QuickNavHomeButton_Click). Carries a
        // destination string ("Movies"/"TvShows"/"Anime") that
        // StremioVideoCenterView.ShowCatalogueBrowsePageAsync records
        // before this page's existing Closed event tears it down, then
        // acts on once its own cleanup finishes - see that method's own
        // remarks for why a "pending destination" field rather than
        // opening the next page from inside this event handler directly.
        public event EventHandler<string> NavigationRequested;

        private bool _isTv;
        private CatalogueService _catalogueService;

        // Persistent hero navigation task: the quick-nav row plus the
        // existing Back button, in on-screen left-to-right order - the
        // single source of truth for Left/Right while any of them has
        // focus (mirrors StremioVideoCenterView's own _topNavButtons/
        // HandleTopNavDirectional convention).
        private Button[] _headerNavButtons;

        private readonly ObservableCollection<MediaItem> _featuredItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _popularItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _newItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _filteredItems = new ObservableCollection<MediaItem>();

        // Guards GenreCombo/YearCombo.SelectedIndex assignments made while
        // populating the combo boxes (PopulateFilterCombos) from also
        // triggering a filter fetch - only a genuine user selection change
        // should start a network request.
        private bool _suppressFilterEvents;

        private string _activeFilterGenre;
        private int? _activeFilterYear;
        private int _filteredSkip;

        // Incremented on every fresh filter application; a filtered-page
        // fetch that completes after a newer one has started discards its
        // own (now-stale) results instead of appending them alongside the
        // new filter's results.
        private int _filteredLoadToken;
        private bool _isLoadingFiltered;

        public CatalogueBrowseView()
        {
            InitializeComponent();

            // Catches the bubbling MediaCard.SelectedEvent raised by the
            // raw MediaCards inside FilteredItemsControl (the filtered-
            // view WrapPanel grid isn't wrapped in a MediaSection, so
            // nothing else already consumes this event on the way up -
            // unlike FeaturedSection/PopularSection/NewSection, which
            // each consume it internally and raise their own typed
            // MediaSelected event instead, handled separately below).
            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(FilteredMediaCard_Selected));

            FeaturedSection.MediaSelected += Section_MediaSelected;
            PopularSection.MediaSelected += Section_MediaSelected;
            NewSection.MediaSelected += Section_MediaSelected;

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

        public void Initialize(bool isTv, CatalogueService catalogueService)
        {
            if (catalogueService == null)
            {
                throw new ArgumentNullException(nameof(catalogueService));
            }

            _isTv = isTv;
            _catalogueService = catalogueService;

            HeaderText.Text = isTv ? "TV Shows" : "Movies";

            FeaturedSection.Items = _featuredItems;
            PopularSection.Items = _popularItems;
            NewSection.Items = _newItems;
            FilteredItemsControl.ItemsSource = _filteredItems;

            PopulateFilterCombos();

            ShowDefaultView();

            BeginLoadDefaultView();
        }

        // Initialize itself stays synchronous (matching the un-awaited
        // "page.Initialize(...)" call site in
        // StremioVideoCenterView.ShowCatalogueBrowsePageAsync) - this
        // async-void shim is the same fire-and-forget-from-a-sync-context
        // pattern already used by MediaCard's own Loaded handler.
        private async void BeginLoadDefaultView()
        {
            await LoadDefaultViewAsync();
        }

        // =========================================================
        // DEFAULT VIEW: FEATURED / POPULAR / NEW RELEASES
        // =========================================================

        private async Task LoadDefaultViewAsync()
        {
            LoadingText.Visibility = Visibility.Visible;
            DefaultEmptyText.Visibility = Visibility.Collapsed;

            int currentYear = DateTime.UtcNow.Year;

            Task<List<MediaItem>> featuredTask = SafeFetchAsync(() =>
                _isTv ? _catalogueService.GetFeaturedTvShowsAsync() : _catalogueService.GetFeaturedMoviesAsync());

            Task<List<MediaItem>> popularTask = SafeFetchAsync(() =>
                _isTv ? _catalogueService.GetPopularTvShowsAsync() : _catalogueService.GetPopularMoviesAsync());

            Task<List<MediaItem>> newTask = SafeFetchAsync(() =>
                _isTv
                    ? _catalogueService.GetNewTvShowsAsync(currentYear)
                    : _catalogueService.GetNewMoviesAsync(currentYear));

            await Task.WhenAll(featuredTask, popularTask, newTask);

            List<MediaItem> featured = featuredTask.Result;
            List<MediaItem> popular = popularTask.Result;
            List<MediaItem> newest = newTask.Result;

            LoadingText.Visibility = Visibility.Collapsed;

            ReplaceCollection(_featuredItems, featured);
            ReplaceCollection(_popularItems, popular);
            ReplaceCollection(_newItems, newest);

            FeaturedSection.Visibility = featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PopularSection.Visibility = popular.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NewSection.Visibility = newest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool anyContent = featured.Count > 0 || popular.Count > 0 || newest.Count > 0;
            DefaultEmptyText.Visibility = anyContent ? Visibility.Collapsed : Visibility.Visible;

            Dispatcher.BeginInvoke(new Action(FocusFirstAvailable), DispatcherPriority.Input);
        }

        // CatalogueService's own catalogue-row methods are already fail-
        // soft in practice (Cinemeta-first with a TMDB fallback, both
        // wrapped defensively - see CatalogueService.GetCatalogueRowAsync)
        // but this is a defensive backstop so one failing row can never
        // take the other two down with it via Task.WhenAll.
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
        // FILTER BAR
        // =========================================================

        private void PopulateFilterCombos()
        {
            _suppressFilterEvents = true;

            GenreCombo.Items.Clear();
            GenreCombo.Items.Add("Any");

            IReadOnlyList<string> genres = _isTv ? CatalogueService.SeriesGenres : CatalogueService.MovieGenres;

            foreach (string genre in genres)
            {
                GenreCombo.Items.Add(genre);
            }

            GenreCombo.SelectedIndex = 0;

            YearCombo.Items.Clear();
            YearCombo.Items.Add("Any");

            IReadOnlyList<int> years = _isTv ? CatalogueService.SeriesYears : CatalogueService.MovieYears;

            foreach (int year in years)
            {
                YearCombo.Items.Add(year.ToString());
            }

            YearCombo.SelectedIndex = 0;

            _suppressFilterEvents = false;
        }

        private string GetSelectedGenre()
        {
            string selected = GenreCombo.SelectedItem as string;
            return string.IsNullOrEmpty(selected) || selected == "Any" ? null : selected;
        }

        private int? GetSelectedYear()
        {
            string selected = YearCombo.SelectedItem as string;

            if (string.IsNullOrEmpty(selected) || selected == "Any")
            {
                return null;
            }

            int year;
            return int.TryParse(selected, out year) ? (int?)year : null;
        }

        private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterEvents)
            {
                return;
            }

            await ApplyFiltersAsync();
        }

        // Cinemeta's "New Releases" (year) catalog uses its own "genre"
        // extra to carry the REQUIRED year value (live-verified this
        // slice - see CatalogueService's class remarks), so a category
        // genre can't also be sent on the same request: the protocol has
        // no slot for both at once. Rather than silently dropping one of
        // the two selections, Year takes priority when both are picked
        // and FilterLimitationText says so explicitly - this is a real,
        // documented provider limitation, not a bug or a hidden gap.
        private async Task ApplyFiltersAsync()
        {
            string genre = GetSelectedGenre();
            int? year = GetSelectedYear();

            _activeFilterGenre = genre;
            _activeFilterYear = year;

            if (genre == null && year == null)
            {
                FilterLimitationText.Text = "No rating filter - Cinemeta doesn't support one.";
                ShowDefaultView();
                return;
            }

            FilterLimitationText.Text = (year.HasValue && !string.IsNullOrEmpty(genre))
                ? "Genre filtering isn't available together with a Year filter - Cinemeta's New Releases catalog uses its year value in place of genre. Showing New Releases for " + year.Value + "."
                : "No rating filter - Cinemeta doesn't support one.";

            _filteredSkip = 0;
            _filteredItems.Clear();
            FilteredEmptyText.Visibility = Visibility.Collapsed;
            LoadMoreButton.Visibility = Visibility.Collapsed;

            ShowFilteredView();

            await LoadFilteredPageAsync(focusFirstResult: true);
        }

        // =========================================================
        // FILTERED VIEW: SINGLE GRID + LOAD MORE
        // =========================================================

        private async Task LoadFilteredPageAsync(bool focusFirstResult)
        {
            _isLoadingFiltered = true;
            LoadMoreButton.IsEnabled = false;

            int requestSkip = _filteredSkip;
            int loadToken = ++_filteredLoadToken;

            if (requestSkip == 0)
            {
                LoadingText.Visibility = Visibility.Visible;
            }

            List<MediaItem> page;

            try
            {
                if (_activeFilterYear.HasValue)
                {
                    page = _isTv
                        ? await _catalogueService.GetNewTvShowsAsync(_activeFilterYear.Value, FilteredPageSize, requestSkip)
                        : await _catalogueService.GetNewMoviesAsync(_activeFilterYear.Value, FilteredPageSize, requestSkip);
                }
                else
                {
                    page = _isTv
                        ? await _catalogueService.GetPopularTvShowsAsync(FilteredPageSize, requestSkip, _activeFilterGenre)
                        : await _catalogueService.GetPopularMoviesAsync(FilteredPageSize, requestSkip, _activeFilterGenre);
                }
            }
            catch
            {
                page = new List<MediaItem>();
            }

            if (loadToken != _filteredLoadToken)
            {
                // A newer filter selection superseded this request while
                // it was still in flight - its results are stale and are
                // discarded rather than mixed in with the new filter's.
                return;
            }

            LoadingText.Visibility = Visibility.Collapsed;

            foreach (MediaItem item in page)
            {
                _filteredItems.Add(item);
            }

            _filteredSkip = requestSkip + page.Count;

            bool hasAny = _filteredItems.Count > 0;
            FilteredEmptyText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

            // A short page (fewer items than requested) is treated as "no
            // more results" - Cinemeta's own catalog protocol doesn't
            // expose a total-count field to track against instead.
            LoadMoreButton.Visibility = (hasAny && page.Count >= FilteredPageSize) ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreButton.IsEnabled = true;

            _isLoadingFiltered = false;

            if (focusFirstResult && hasAny)
            {
                Dispatcher.BeginInvoke(new Action(() => FocusFilteredCard(0)), DispatcherPriority.Input);
            }
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFiltered)
            {
                return;
            }

            await LoadFilteredPageAsync(focusFirstResult: false);
        }

        private void FilteredMediaCard_Selected(object sender, RoutedEventArgs e)
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
        // VIEW SWITCHING
        // =========================================================

        private void ShowDefaultView()
        {
            DefaultScrollViewer.Visibility = Visibility.Visible;
            FilteredScrollViewer.Visibility = Visibility.Collapsed;
        }

        private void ShowFilteredView()
        {
            DefaultScrollViewer.Visibility = Visibility.Collapsed;
            FilteredScrollViewer.Visibility = Visibility.Visible;
        }

        private bool IsFilteredViewActive()
        {
            return FilteredScrollViewer.Visibility == Visibility.Visible;
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

        // Persistent hero navigation task: Home has no separate page to
        // switch to (Home is StremioVideoCenterView's own base content,
        // always present underneath) - so reusing RaiseClosed() directly
        // is the correct, complete behaviour, identical to the Back
        // button.
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

            // Reuses the exact same teardown path as the Back button -
            // StremioVideoCenterView's own completion-source/cleanup
            // logic doesn't need to know a destination was requested; it
            // only needs to check for one after this page has already
            // finished closing (see ShowCatalogueBrowsePageAsync).
            RaiseClosed();
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void CatalogueBrowseView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    // Filter controls task: Escape while a filter's
                    // dropdown is open dismisses the dropdown, matching
                    // the same "cancel the immediate thing, not the
                    // whole page" behaviour this Escape already implies
                    // elsewhere - without this, Escape would close the
                    // entire Movies/TV page out from under an open
                    // filter popup.
                    if (IsAnyFilterDropDownOpen())
                    {
                        CloseAnyFilterDropDown();
                    }
                    else
                    {
                        RaiseClosed();
                    }

                    e.Handled = true;
                    break;

                case Key.Enter:
                case Key.Space:
                    // Filter controls task: activates the focused filter
                    // (opens its dropdown, or confirms/closes it if
                    // already open) - mirrors ControllerInput.A's
                    // ActivateFocusedElement below. Left unhandled (and
                    // so left to bubble/tunnel further) when the filter
                    // bar doesn't have focus, so this never touches the
                    // Back button's or a MediaCard's own existing
                    // Enter/Space activation.
                    if (ActivateFilterCombo())
                    {
                        e.Handled = true;
                    }

                    break;

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
                    // Filter controls task: B while a filter's dropdown
                    // is open dismisses the dropdown rather than closing
                    // the whole page out from under it - same reasoning
                    // as the Escape case above, which this mirrors for
                    // the controller path.
                    if (IsAnyFilterDropDownOpen())
                    {
                        CloseAnyFilterDropDown();
                        return true;
                    }

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
        // the Back button when it has focus. StremioVideoCenterView's own
        // Enter/Space handling already does this generically for any
        // focused ButtonBase, but StremioVideoCenterView.HandleControllerInput
        // fully delegates controller input to this page's own
        // HandleControllerInput while it's open ("most specific overlay
        // wins"), so without this, controller A here would only ever
        // reach ActivateFocusedCard (MediaCard-only) and never the Back
        // button.
        private bool ActivateFocusedElement()
        {
            // Filter controls task: checked first, since GenreCombo/
            // YearCombo are neither a ButtonBase nor a MediaCard - without
            // this, controller A on a focused filter would silently fall
            // through both checks below and do nothing.
            if (ActivateFilterCombo())
            {
                return true;
            }

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
        // FILTER BAR OPERATION (filter controls task)
        // =========================================================
        //
        // GenreCombo/YearCombo are ordinary WPF ComboBoxes, but this
        // view's PreviewKeyDown/HandleControllerInput already claim every
        // Up/Down/Left/Right/Enter/A themselves before a focused child
        // control would ever see them (same "hand-rolled focus
        // management instead of WPF's built-in directional navigation"
        // convention this whole view already uses - see the class
        // remarks), so a focused combo would never naturally receive an
        // arrow key or Enter/A on its own. This section gives the filter
        // bar its own small, self-contained operation model instead,
        // reusing the WPF ComboBox exactly as it already worked for the
        // mouse (same Items/SelectedIndex/SelectionChanged wiring, same
        // FilterComboItemStyle dropdown) - just driven explicitly rather
        // than through WPF's normal input plumbing.

        // Dropdown-focus fix (ported from AnimeBrowseView's own proven
        // fix for the identical bug): opening a ComboBox's dropdown makes
        // WPF move real keyboard focus from the ComboBox itself onto its
        // currently-selected ComboBoxItem inside the popup - confirmed
        // live there (IsKeyboardFocused on the ComboBox goes false,
        // IsKeyboardFocusWithin stays true), so the plain IsKeyboardFocused
        // checks this method used before went blind the instant the
        // dropdown opened. Every caller below (MoveFilterDropDownSelection
        // for Up/Down, ActivateFilterCombo for a second Enter/A to
        // confirm/close) would then silently no-op, leaving WPF's own
        // native ComboBoxItem key handling to run instead - which tracks
        // its own "highlighted" preview item independently of
        // SelectedIndex, so arrowing through the list looked like it
        // moved something but never changed what Filter_SelectionChanged
        // actually applied. ActivateFilterCombo below re-asserts focus on
        // the ComboBox itself right after toggling it open, but that
        // re-focus is deferred a frame (see its own remark), so this also
        // accepts IsKeyboardFocusWithin while the dropdown is open as a
        // fallback for the brief window before that runs.
        private ComboBox GetFocusedFilterCombo()
        {
            if (GenreCombo.IsKeyboardFocused || (GenreCombo.IsDropDownOpen && GenreCombo.IsKeyboardFocusWithin))
            {
                return GenreCombo;
            }

            if (YearCombo.IsKeyboardFocused || (YearCombo.IsDropDownOpen && YearCombo.IsKeyboardFocusWithin))
            {
                return YearCombo;
            }

            return null;
        }

        private bool IsFilterBarFocused()
        {
            return GetFocusedFilterCombo() != null;
        }

        private bool IsAnyFilterDropDownOpen()
        {
            return GenreCombo.IsDropDownOpen || YearCombo.IsDropDownOpen;
        }

        private void CloseAnyFilterDropDown()
        {
            GenreCombo.IsDropDownOpen = false;
            YearCombo.IsDropDownOpen = false;
        }

        private void MoveFilterBarHorizontal(int direction)
        {
            if (direction < 0)
            {
                GenreCombo.Focus();
            }
            else
            {
                YearCombo.Focus();
            }
        }

        // A/Enter on the filter bar: opens the focused combo's dropdown
        // if closed, or confirms (closes) it if already open. The
        // already-current SelectedIndex is whatever MoveFilterDropDown
        // Selection below last set, exactly mirroring how a mouse click
        // on an item already applies the selection immediately via the
        // existing Filter_SelectionChanged handler - "confirm" here is
        // only about dismissing the popup, not a separate apply step.
        // Returns false (does nothing) when the filter bar doesn't have
        // focus, so callers can fall through to their own activation
        // logic for whatever else is focused.
        // Dropdown-focus fix (ported from AnimeBrowseView): re-asserts
        // focus on the ComboBox itself right after toggling it open, since
        // WPF would otherwise hand real keyboard focus to the newly-
        // visible ComboBoxItem instead of leaving it on the ComboBox - see
        // GetFocusedFilterCombo's own remark for the full explanation.
        // Deferred past the layout pass that does that hand-off via
        // DispatcherPriority.Input, same convention AnimeBrowseView's own
        // copy already uses for post-layout focus. Needed in both
        // directions - right after opening, and again after closing,
        // since closing the popup leaves focus sitting on the now-hidden
        // item otherwise.
        private bool ActivateFilterCombo()
        {
            ComboBox combo = GetFocusedFilterCombo();

            if (combo == null)
            {
                return false;
            }

            combo.IsDropDownOpen = !combo.IsDropDownOpen;

            Dispatcher.BeginInvoke(new Action(() => combo.Focus()), DispatcherPriority.Input);

            return true;
        }

        // Moves the focused filter's selection directly by one item
        // rather than letting WPF's own arrow-key-in-open-dropdown
        // handling run (which this view's own tunneling PreviewKeyDown/
        // HandleControllerInput would still intercept before it ever
        // reached the ComboBox). Changing SelectedIndex already fires
        // the existing Filter_SelectionChanged -> ApplyFiltersAsync
        // wiring unchanged, so this reuses that exact path rather than
        // adding a second one. Explicitly brings the newly-selected
        // item's container into view rather than relying on WPF's
        // default ComboBox scroll-into-view behaviour, which is tied to
        // that control's own default template's named parts and isn't
        // guaranteed to apply to this custom one.
        private void MoveFilterDropDownSelection(int direction)
        {
            ComboBox combo = GetFocusedFilterCombo();

            if (combo == null || combo.Items.Count == 0)
            {
                return;
            }

            int target = combo.SelectedIndex + direction;
            target = Math.Max(0, Math.Min(target, combo.Items.Count - 1));

            combo.SelectedIndex = target;

            FrameworkElement container =
                combo.ItemContainerGenerator.ContainerFromIndex(target) as FrameworkElement;

            container?.BringIntoView();
        }

        // =========================================================
        // FOCUS MOVEMENT (shared by keyboard + controller)
        // =========================================================

        private void MoveVertical(int direction)
        {
            // Filter controls task: while a filter's dropdown is open,
            // Up/Down move its own selection instead of navigating this
            // page - checked first, ahead of every other case below.
            // Checked via IsAnyFilterDropDownOpen() rather than which
            // control currently has focus, since opening the dropdown
            // itself moves real keyboard focus off the ComboBox (see
            // GetFocusedFilterCombo/ActivateFilterCombo's own dropdown-
            // focus-fix remarks) - IsDropDownOpen is the state that
            // actually stays reliable for the duration the popup is up.
            if (IsAnyFilterDropDownOpen())
            {
                MoveFilterDropDownSelection(direction);
                return;
            }

            // Dedicated-page Back navigation task, extended by the filter
            // controls task and the persistent hero navigation task: the
            // chain is now header row (quick-nav + Back) <-> filter bar
            // <-> content (mirroring Home's own Hero <-> Watch Now/My
            // List <-> content rows chain) rather than Back going
            // straight to content - Down from anywhere in the header row
            // now enters the filter bar first, and Up from the header row
            // is still a no-op (it's the topmost layer).
            if (IsHeaderRowFocused())
            {
                if (direction > 0)
                {
                    GenreCombo.Focus();
                }

                return;
            }

            if (IsFilterBarFocused())
            {
                if (direction < 0)
                {
                    CloseButton.Focus();
                }
                else
                {
                    FocusFirstAvailableAnyView();
                }

                return;
            }

            if (IsFilteredViewActive())
            {
                if (direction < 0 && IsFocusedFilteredCardInTopRow())
                {
                    // Was CloseButton.Focus() - Up from the topmost
                    // filtered row now reaches the filter bar first, not
                    // Back directly, completing the three-layer chain.
                    GenreCombo.Focus();
                    return;
                }

                MoveFilteredGrid(0, direction);
            }
            else
            {
                MoveDefaultVertical(direction);
            }
        }

        private void MoveHorizontal(int direction)
        {
            // Filter controls task: Left/Right while a dropdown is open
            // would otherwise step focus to the other filter with the
            // popup still showing - consumed as a no-op instead, exactly
            // mirroring why Up/Down are repurposed above rather than
            // left to their normal meaning while a dropdown is open.
            if (IsAnyFilterDropDownOpen())
            {
                return;
            }

            // Persistent hero navigation task: checked first (mirrors
            // where IsHeaderRowFocused is checked first in MoveVertical
            // above) - Left/Right while any header-row button has focus
            // cycles through the quick-nav buttons and Back, and clamps
            // at either end rather than falling through to the filter
            // bar/content below.
            if (IsHeaderRowFocused())
            {
                MoveHeaderRowHorizontal(direction);
                return;
            }

            if (IsFilterBarFocused())
            {
                MoveFilterBarHorizontal(direction);
                return;
            }

            if (IsFilteredViewActive())
            {
                MoveFilteredGrid(direction, 0);
            }
            else
            {
                MoveDefaultHorizontal(direction);
            }
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

        private MediaSection[] GetVisibleDefaultSections()
        {
            return new[] { FeaturedSection, PopularSection, NewSection }
                .Where(section => section.Visibility == Visibility.Visible)
                .ToArray();
        }

        private MediaSection GetFocusedDefaultSection()
        {
            foreach (MediaSection section in GetVisibleDefaultSections())
            {
                if (section.IsKeyboardFocusWithin)
                {
                    return section;
                }
            }

            return null;
        }

        private void MoveDefaultVertical(int direction)
        {
            MediaSection[] sections = GetVisibleDefaultSections();

            if (sections.Length == 0)
            {
                CloseButton.Focus();
                return;
            }

            MediaSection current = GetFocusedDefaultSection();
            int index = Array.IndexOf(sections, current);

            if (index < 0)
            {
                sections[0].FocusFirstCard();
                return;
            }

            int target = index + direction;

            if (target < 0)
            {
                // Dedicated-page Back navigation task, extended by the
                // filter controls task: moving up out of the topmost
                // section reaches the filter bar (not Back directly
                // anymore) instead of clamping in place.
                GenreCombo.Focus();
                return;
            }

            target = Math.Min(target, sections.Length - 1);
            sections[target].FocusFirstCard();
        }

        private void MoveDefaultHorizontal(int direction)
        {
            MediaSection current = GetFocusedDefaultSection();
            current?.MoveHorizontal(direction);
        }

        private void FocusFirstAvailable()
        {
            foreach (MediaSection section in GetVisibleDefaultSections())
            {
                section.FocusFirstCard();
                return;
            }

            CloseButton.Focus();
        }

        // Dedicated-page Back navigation task: Down from the Back button
        // needs to reach whichever content view is actually showing
        // (default or filtered) - FocusFirstAvailable above only ever
        // knew about the default view's three MediaSections.
        private void FocusFirstAvailableAnyView()
        {
            if (IsFilteredViewActive())
            {
                if (FilteredItemsControl.Items.Count > 0)
                {
                    FocusFilteredCard(0);
                }
                else
                {
                    CloseButton.Focus();
                }

                return;
            }

            FocusFirstAvailable();
        }

        private bool IsFocusedFilteredCardInTopRow()
        {
            int index = GetFocusedFilteredCardIndex();
            return index >= 0 && index < GetFilteredColumnsPerRow();
        }

        // =========================================================
        // FILTERED GRID FOCUS MOVEMENT
        // =========================================================
        //
        // The filtered grid is a plain WrapPanel of fixed-width
        // MediaCards rather than a MediaSection, so it has no built-in
        // FocusFirstCard/MoveHorizontal of its own - this mirrors that
        // same shape (index math + TransformToAncestor-free container
        // lookup via ItemContainerGenerator, matching MediaSection's own
        // FindVisualChild pattern) using an estimated columns-per-row
        // (see FilteredCardWidth's remarks) since WPF doesn't expose the
        // WrapPanel's actual row layout directly.

        private int GetFilteredColumnsPerRow()
        {
            double width = FilteredItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            int columns = (int)(width / FilteredCardWidth);

            return Math.Max(1, columns);
        }

        private void MoveFilteredGrid(int deltaColumns, int deltaRows)
        {
            int count = FilteredItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedFilteredCardIndex();

            if (current < 0)
            {
                FocusFilteredCard(0);
                return;
            }

            int columns = GetFilteredColumnsPerRow();
            int target = current + deltaColumns + (deltaRows * columns);

            target = Math.Max(0, Math.Min(target, count - 1));

            FocusFilteredCard(target);
        }

        private int GetFocusedFilteredCardIndex()
        {
            for (int i = 0; i < FilteredItemsControl.Items.Count; i++)
            {
                ContentPresenter presenter =
                    FilteredItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

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

        private void FocusFilteredCard(int index)
        {
            if (index < 0 || index >= FilteredItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                FilteredItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusFilteredCard(index)),
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
