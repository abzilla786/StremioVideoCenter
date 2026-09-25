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

        private bool _isTv;
        private CatalogueService _catalogueService;

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

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void CatalogueBrowseView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    RaiseClosed();
                    e.Handled = true;
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
        // FOCUS MOVEMENT (shared by keyboard + controller)
        // =========================================================

        private void MoveVertical(int direction)
        {
            if (IsFilteredViewActive())
            {
                MoveFilteredGrid(0, direction);
            }
            else
            {
                MoveDefaultVertical(direction);
            }
        }

        private void MoveHorizontal(int direction)
        {
            if (IsFilteredViewActive())
            {
                MoveFilteredGrid(direction, 0);
            }
            else
            {
                MoveDefaultHorizontal(direction);
            }
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
                return;
            }

            MediaSection current = GetFocusedDefaultSection();
            int index = Array.IndexOf(sections, current);

            if (index < 0)
            {
                sections[0].FocusFirstCard();
                return;
            }

            int target = Math.Max(0, Math.Min(index + direction, sections.Length - 1));
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
