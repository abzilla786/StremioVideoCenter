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
    // ANIME BROWSE VIEW (Slice 15 - dedicated Anime page; Slice 17 -
    // Kitsu locked in as Zino's permanent anime provider; Slice 17
    // UI/navigation fixes - AnimeFLV section removed)
    // =============================================================
    //
    // A single content area: BROWSE ANIME (Kitsu) - a combinable,
    // server-side filtered discovery experience against Kitsu's own API
    // (KitsuService.BrowseAnimeAsync) - Type/Category/Season/Year/
    // Status/Sort, all combinable, applied on demand via the Apply
    // Filters button. Replaces the old Featured/Popular/New
    // rows and the old single-row "Popular on Kitsu" row entirely.
    // Category filter
    // options are derived from fetched results, never a hard-coded list
    // (see RegisterDiscoveredCategories) - Kitsu has no dedicated
    // categories-listing endpoint in its documented API.
    //
    // The previous "Browse AnimeFLV" genre/search section (a browser
    // against AnimeFlvCatalogueService's own catalogs) has been removed
    // from this page per the Slice 17 UI/navigation fix request - the
    // Anime page is now Kitsu-only. AnimeFlvCatalogueService itself and
    // its addon registration are untouched (this removal is scoped to
    // this page's UI only - see that class for where it may still be
    // used elsewhere).
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

        // Kitsu's own documented/enforced page[limit] maximum (see
        // KitsuService.MaxPageLimit) - used as this page's own Kitsu
        // Browse page size.
        private const int KitsuBrowsePageSize = 20;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        // Persistent hero navigation task: same quick-nav row event as
        // CatalogueBrowseView's own NavigationRequested - see that file's
        // remarks.
        public event EventHandler<string> NavigationRequested;

        private KitsuService _kitsuService;

        // Persistent hero navigation task: same header-row Left/Right
        // source-of-truth convention as CatalogueBrowseView's own
        // _headerNavButtons.
        private Button[] _headerNavButtons;

        // Slice 17 - Kitsu Browse filter combos, in Left/Right cycling
        // order (same _headerNavButtons array convention).
        private ComboBox[] _kitsuFilterCombos;

        private readonly ObservableCollection<MediaItem> _kitsuBrowseItems = new ObservableCollection<MediaItem>();

        // Category chips - an ObservableCollection so newly-discovered
        // categories can be appended (Add) without disturbing already-
        // realized containers, which would otherwise reset any chip the
        // user had already toggled on (see RegisterDiscoveredCategories).
        private readonly ObservableCollection<KitsuCategoryOption> _discoveredCategories = new ObservableCollection<KitsuCategoryOption>();
        private readonly HashSet<string> _knownCategorySlugs = new HashSet<string>();

        private int _kitsuBrowseOffset;
        private int _kitsuBrowseTotalCount;
        private int _kitsuBrowseLoadToken;
        private bool _isLoadingKitsuBrowse;

        public AnimeBrowseView()
        {
            InitializeComponent();

            // Mouse click-and-drag scrolling (Slice 20 Stage 3).
            DragScroll.Attach(PageScrollViewer, DragScrollAxis.Vertical);

            // Catches the bubbling MediaCard.SelectedEvent from the raw
            // MediaCards inside KitsuBrowseItemsControl - see
            // CatalogueBrowseView's identical remark on
            // FilteredMediaCard_Selected for why this is needed (the
            // WrapPanel grid isn't wrapped in a MediaSection, so nothing
            // else already consumes the event on the way up).
            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(MediaCard_Selected));

            _headerNavButtons =
                new[]
                {
                    QuickNavHomeButton,
                    QuickNavMoviesButton,
                    QuickNavTvShowsButton,
                    QuickNavAnimeButton,
                    CloseButton
                };

            _kitsuFilterCombos =
                new[]
                {
                    KitsuTypeCombo,
                    KitsuSeasonCombo,
                    KitsuYearCombo,
                    KitsuStatusCombo,
                    KitsuSortCombo
                };
        }

        // =========================================================
        // INITIALIZE
        // =========================================================
        //
        // Takes no CatalogueService - Kitsu (via BrowseAnimeAsync) is now
        // the sole source for this page. Slice 17 UI/navigation fixes:
        // no longer takes an AnimeFlvCatalogueService either - the
        // "Browse AnimeFLV" section has been removed from this page (see
        // class remarks).
        public void Initialize(KitsuService kitsuService)
        {
            if (kitsuService == null)
            {
                throw new ArgumentNullException(nameof(kitsuService));
            }

            _kitsuService = kitsuService;

            KitsuBrowseItemsControl.ItemsSource = _kitsuBrowseItems;
            KitsuCategoryItemsControl.ItemsSource = _discoveredCategories;

            PopulateKitsuFilterCombos();

            BeginLoadKitsuBrowse();

            // UI/navigation fix: the page previously received no initial
            // keyboard/controller focus at all when entering from Hero/
            // quick-nav, leaving Up/Down/Left/Right dead until the user
            // clicked something first. Same Dispatcher.BeginInvoke(...,
            // DispatcherPriority.Input) pattern CatalogueBrowseView's own
            // LoadDefaultViewAsync uses for its initial focus - run here
            // rather than after the Kitsu Browse load completes, since
            // the filter row is already fully populated and focusable as
            // soon as Initialize returns. Every visit to this page gets a
            // brand-new AnimeBrowseView instance (see
            // StremioVideoCenterView.ShowAnimePageAsync), so there is no
            // prior focus state for this instance to preserve or
            // restore - unconditionally focusing the first filter control
            // here matches CatalogueBrowseView's own identical precedent.
            Dispatcher.BeginInvoke(new Action(FocusFirstAvailable), DispatcherPriority.Input);
        }

        private async void BeginLoadKitsuBrowse()
        {
            await LoadKitsuBrowsePageAsync(focusFirstResult: false);
        }

        // =========================================================
        // KITSU BROWSE FILTER COMBOS (Slice 17)
        // =========================================================
        //
        // KitsuFilterOption pairs a display label with the literal
        // Kitsu filter/sort value BrowseAnimeAsync expects (or null for
        // "All"/"Any" - no filter sent for that dimension) - every
        // value here is one live-verified this slice against a real
        // kitsu.io response (see KitsuService.BrowseAnimeAsync remarks),
        // never guessed from documentation alone.
        private class KitsuFilterOption
        {
            public string Label;
            public string Value;

            public override string ToString()
            {
                return Label;
            }
        }

        // One discovered category, shown as a toggle chip. IsSelected is
        // plain (no INotifyPropertyChanged) - the ToggleButton's own
        // IsChecked TwoWay binding pushes user toggles into it, and this
        // class never needs to push a value back out to the UI from code
        // (selected slugs are read directly at Apply-time - see
        // GetSelectedCategorySlugs).
        private class KitsuCategoryOption
        {
            public string Title { get; set; }
            public string Slug { get; set; }
            public bool IsSelected { get; set; }
        }

        private void PopulateKitsuFilterCombos()
        {
            PopulateCombo(KitsuTypeCombo, new[]
            {
                new KitsuFilterOption { Label = "All", Value = null },
                new KitsuFilterOption { Label = "TV", Value = "TV" },
                new KitsuFilterOption { Label = "Movie", Value = "movie" },
                new KitsuFilterOption { Label = "OVA", Value = "OVA" },
                new KitsuFilterOption { Label = "ONA", Value = "ONA" },
                new KitsuFilterOption { Label = "Special", Value = "special" }
            });

            PopulateCombo(KitsuSeasonCombo, new[]
            {
                new KitsuFilterOption { Label = "Any", Value = null },
                new KitsuFilterOption { Label = "Winter", Value = "winter" },
                new KitsuFilterOption { Label = "Spring", Value = "spring" },
                new KitsuFilterOption { Label = "Summer", Value = "summer" },
                new KitsuFilterOption { Label = "Fall", Value = "fall" }
            });

            var yearOptions = new List<KitsuFilterOption> { new KitsuFilterOption { Label = "Any", Value = null } };

            // A generous, simple descending range - Kitsu's own catalogue
            // goes back decades and this needs no live catalogue lookup
            // to build (unlike categories, a year is not something that
            // benefits from being "discovered" from results).
            int currentYear = DateTime.Now.Year;

            for (int year = currentYear + 1; year >= 1960; year--)
            {
                yearOptions.Add(new KitsuFilterOption { Label = year.ToString(), Value = year.ToString() });
            }

            PopulateCombo(KitsuYearCombo, yearOptions);

            PopulateCombo(KitsuStatusCombo, new[]
            {
                new KitsuFilterOption { Label = "Any", Value = null },
                new KitsuFilterOption { Label = "Currently Airing", Value = "current" },
                new KitsuFilterOption { Label = "Finished", Value = "finished" },
                new KitsuFilterOption { Label = "Upcoming", Value = "upcoming" },
                new KitsuFilterOption { Label = "TBA", Value = "tba" },
                new KitsuFilterOption { Label = "Unreleased", Value = "unreleased" }
            });

            PopulateCombo(KitsuSortCombo, new[]
            {
                new KitsuFilterOption { Label = "Popular", Value = "popularityRank" },
                new KitsuFilterOption { Label = "Top Rated", Value = "ratingRank" },
                new KitsuFilterOption { Label = "Newest", Value = "-startDate" }
            });
        }

        private static void PopulateCombo(ComboBox combo, IEnumerable<KitsuFilterOption> options)
        {
            combo.Items.Clear();

            foreach (KitsuFilterOption option in options)
            {
                combo.Items.Add(option);
            }

            combo.SelectedIndex = 0;
        }

        // =========================================================
        // KITSU BROWSE (Slice 17 - replaces the old rows and the
        // old single-row "Popular on Kitsu" row)
        // =========================================================

        private async void KitsuApplyFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            await RestartKitsuBrowseAsync();
        }

        private async Task RestartKitsuBrowseAsync()
        {
            _kitsuBrowseOffset = 0;
            _kitsuBrowseTotalCount = 0;
            _kitsuBrowseItems.Clear();
            KitsuBrowseEmptyText.Visibility = Visibility.Collapsed;
            KitsuBrowseLoadMoreButton.Visibility = Visibility.Collapsed;

            await LoadKitsuBrowsePageAsync(focusFirstResult: true);
        }

        private async Task LoadKitsuBrowsePageAsync(bool focusFirstResult)
        {
            _isLoadingKitsuBrowse = true;
            KitsuBrowseLoadMoreButton.IsEnabled = false;

            int requestOffset = _kitsuBrowseOffset;
            int loadToken = ++_kitsuBrowseLoadToken;

            if (requestOffset == 0)
            {
                KitsuBrowseLoadingText.Visibility = Visibility.Visible;
            }

            List<string> categorySlugs = GetSelectedCategorySlugs();
            string subtype = (KitsuTypeCombo.SelectedItem as KitsuFilterOption)?.Value;
            string season = (KitsuSeasonCombo.SelectedItem as KitsuFilterOption)?.Value;
            string yearValue = (KitsuYearCombo.SelectedItem as KitsuFilterOption)?.Value;
            int? seasonYear = !string.IsNullOrWhiteSpace(yearValue) ? int.Parse(yearValue) : (int?)null;
            string status = (KitsuStatusCombo.SelectedItem as KitsuFilterOption)?.Value;
            string sort = (KitsuSortCombo.SelectedItem as KitsuFilterOption)?.Value;

            KitsuBrowseResult result;

            try
            {
                result = await _kitsuService.BrowseAnimeAsync(
                    categorySlugs,
                    season,
                    seasonYear,
                    status,
                    subtype,
                    sort,
                    requestOffset,
                    KitsuBrowsePageSize);
            }
            catch
            {
                result = new KitsuBrowseResult { Items = new List<KitsuAnimeResource>(), TotalCount = 0, Failed = true };
            }

            if (loadToken != _kitsuBrowseLoadToken)
            {
                // A newer Apply superseded this request while it was
                // still in flight - discard rather than mix stale
                // results in with the new filter selection's.
                return;
            }

            KitsuBrowseLoadingText.Visibility = Visibility.Collapsed;

            List<KitsuAnimeResource> page = result?.Items ?? new List<KitsuAnimeResource>();

            RegisterDiscoveredCategories(page);

            List<MediaItem> mapped = page
                .Select(MapKitsuToMediaItem)
                .Where(item => item != null)
                .ToList();

            foreach (MediaItem item in mapped)
            {
                _kitsuBrowseItems.Add(item);
            }

            _kitsuBrowseOffset = requestOffset + page.Count;

            // Slice 21 (F9): a failed request keeps the known total (it used
            // to be overwritten with 0, which hid Load More and left no way
            // to retry), and is reported as a failure rather than as "no
            // anime found".
            bool failed = result != null && result.Failed;

            if (!failed)
            {
                _kitsuBrowseTotalCount = result?.TotalCount ?? _kitsuBrowseItems.Count;
            }

            bool hasAny = _kitsuBrowseItems.Count > 0;
            KitsuBrowseEmptyText.Text = failed
                ? "Couldn't load anime right now. Check your connection, then choose Apply Filters to try again."
                : "No anime found for these filters.";
            KitsuBrowseEmptyText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

            // Real pagination against Kitsu's own meta.count total (see
            // KitsuBrowseResult) - unlike AnimeFLV's own short-page
            // heuristic (that addon exposes no total-count field), Kitsu
            // does, so this only offers another page when one actually
            // exists.
            KitsuBrowseLoadMoreButton.Content = failed ? "Try Again" : "Load More";
            KitsuBrowseLoadMoreButton.Visibility =
                (hasAny && (failed || _kitsuBrowseOffset < _kitsuBrowseTotalCount)) ? Visibility.Visible : Visibility.Collapsed;
            KitsuBrowseLoadMoreButton.IsEnabled = true;

            _isLoadingKitsuBrowse = false;

            if (focusFirstResult && hasAny)
            {
                Dispatcher.BeginInvoke(new Action(() => FocusKitsuCard(0)), DispatcherPriority.Input);
            }
        }

        private async void KitsuBrowseLoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoadingKitsuBrowse)
            {
                return;
            }

            // Load More focus fix: LoadKitsuBrowsePageAsync disables this
            // button for the duration of the request, which drops
            // keyboard focus the instant the control the user just
            // activated becomes disabled - WPF never restores it
            // afterward on its own, so every controller/keyboard Load
            // More press would otherwise silently lose focus. Capture the
            // intent up front and restore it once loading finishes.
            bool hadFocus = KitsuBrowseLoadMoreButton.IsKeyboardFocused;

            await LoadKitsuBrowsePageAsync(focusFirstResult: false);

            if (!hadFocus)
            {
                return;
            }

            if (KitsuBrowseLoadMoreButton.Visibility == Visibility.Visible && KitsuBrowseLoadMoreButton.IsEnabled)
            {
                // More pages remain - the button is still here, so focus
                // returns to it exactly where the user left it.
                KitsuBrowseLoadMoreButton.Focus();
            }
            else if (_kitsuBrowseItems.Count > 0)
            {
                // That was the last page - Load More just disappeared
                // out from under the user's focus, so land on the last
                // card instead of leaving focus lost, matching the same
                // fallback MoveVertical's own Up-from-Load-More edge uses.
                FocusKitsuCard(_kitsuBrowseItems.Count - 1);
            }
        }

        // Reads which category chips are currently toggled on - the
        // slugs BrowseAnimeAsync combines with AND semantics (see that
        // method's own remarks on the live-verified comma-delimited
        // filter[categories] behaviour).
        private List<string> GetSelectedCategorySlugs()
        {
            return _discoveredCategories
                .Where(option => option.IsSelected)
                .Select(option => option.Slug)
                .ToList();
        }

        // Appends any category not already known to _discoveredCategories
        // (Add only - never Clear/replace, so an already-toggled chip's
        // IsSelected state survives a later page/Apply that discovers
        // further categories - see this collection's own field remarks).
        // This is how the Anime Browse page's category filter options are
        // derived from real results rather than a hard-coded list, per
        // this slice's explicit instruction.
        private void RegisterDiscoveredCategories(List<KitsuAnimeResource> items)
        {
            if (items == null)
            {
                return;
            }

            var newOptions = new List<KitsuCategoryOption>();

            foreach (KitsuAnimeResource item in items)
            {
                List<KitsuCategoryInfo> categories = item?.Attributes?.Categories;

                if (categories == null)
                {
                    continue;
                }

                foreach (KitsuCategoryInfo category in categories)
                {
                    if (string.IsNullOrWhiteSpace(category?.Slug) ||
                        string.IsNullOrWhiteSpace(category.Title) ||
                        !_knownCategorySlugs.Add(category.Slug))
                    {
                        continue;
                    }

                    newOptions.Add(new KitsuCategoryOption
                    {
                        Title = category.Title,
                        Slug = category.Slug
                    });
                }
            }

            if (newOptions.Count == 0)
            {
                return;
            }

            foreach (KitsuCategoryOption option in newOptions.OrderBy(o => o.Title, StringComparer.OrdinalIgnoreCase))
            {
                _discoveredCategories.Add(option);
            }

            KitsuCategoryHintText.Visibility = Visibility.Collapsed;
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

        private void MediaCard_Selected(object sender, RoutedEventArgs e)
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
            // Filter controls fix: Escape while a filter's dropdown is
            // open dismisses the dropdown instead of closing the whole
            // page out from under it - same reasoning, and the same
            // precedent (CatalogueBrowseView_PreviewKeyDown's identical
            // Escape case), as Movie/TV's filter bar. This page has no
            // text-entry control (removed along with AnimeFLV), so Escape
            // is otherwise still intercepted unconditionally.
            if (e.Key == Key.Escape)
            {
                if (IsAnyKitsuFilterDropDownOpen())
                {
                    CloseAnyKitsuFilterDropDown();
                }
                else
                {
                    RaiseClosed();
                }

                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                case Key.Space:
                    // Filter controls fix: activates the focused filter
                    // combo (opens its dropdown, or confirms/closes it if
                    // already open) - mirrors ControllerInput.A's
                    // ActivateFocusedElement below and CatalogueBrowseView's
                    // identical Key.Enter/Key.Space case. Left unhandled
                    // when the filter row doesn't have focus, so this
                    // never touches a MediaCard's or Button's own existing
                    // Enter/Space activation (native WPF ButtonBase
                    // behaviour already handles those once focused).
                    //
                    // Category activation fix: tried next when the filter
                    // row doesn't have focus, so a focused category chip
                    // toggles on Enter too - previously Space alone
                    // happened to already toggle a chip (left unhandled
                    // here, it fell through to the ToggleButton's own
                    // native Space handling), but Enter did nothing at
                    // all, since WPF never binds Enter to a ToggleButton's
                    // Click the way it does for Space. Routing both keys
                    // through the same explicit toggle here makes them
                    // behave identically and explicitly, rather than
                    // leaving Space to work only by accident.
                    if (ActivateKitsuFilterCombo())
                    {
                        e.Handled = true;
                    }
                    else if (ActivateKitsuCategoryChip())
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
                    // Filter controls fix: same dropdown-first precedence
                    // as the keyboard Escape case above, and the same
                    // precedent as CatalogueBrowseView's own B handling.
                    if (IsAnyKitsuFilterDropDownOpen())
                    {
                        CloseAnyKitsuFilterDropDown();
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
        // the Back button when it has focus - see CatalogueBrowseView's
        // identical remark on its own ActivateFocusedElement for why this
        // page's own HandleControllerInput (not the generic keyboard
        // Enter/Space path) needs this.
        private bool ActivateFocusedElement()
        {
            // Filter controls fix: checked first, since the Kitsu filter
            // combos are neither a ButtonBase nor a MediaCard - without
            // this, controller A on a focused filter combo would silently
            // fall through both checks below and do nothing, exactly
            // mirroring why CatalogueBrowseView's own ActivateFocusedElement
            // checks its ActivateFilterCombo() first.
            if (ActivateKitsuFilterCombo())
            {
                return true;
            }

            // Category activation fix: checked next, and specifically
            // ahead of the generic ButtonBase fallback right below - a
            // category chip IS a ButtonBase (ToggleButton derives from
            // it), so without this check that fallback would silently
            // swallow controller A on a focused chip (returning true,
            // "handled") without ever actually toggling it. See
            // ActivateKitsuCategoryChip's own remark for why.
            if (ActivateKitsuCategoryChip())
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

        // Category activation fix: A/Enter on a focused category chip
        // toggles it the same way a mouse click already does - flips the
        // ToggleButton's own IsChecked, which the chip's
        // IsChecked="{Binding IsSelected, Mode=TwoWay}" binding pushes
        // straight into the bound KitsuCategoryOption, exactly as a click
        // does. This can't reuse the generic ButtonBase fallback above
        // (RaiseEvent(ButtonBase.ClickEvent)): ToggleButton's own
        // OnClick() override is what flips IsChecked, and it runs BEFORE
        // that override raises Click - externally raising the Click
        // routed event only replays the "something was clicked"
        // notification, it never invokes the override that does the
        // actual toggling, so that fallback alone reported "handled"
        // without ever checking/unchecking the chip (the live bug this
        // fixes). Toggling here never touches Apply Filters - selected
        // categories are only read at Apply-time (GetSelectedCategorySlugs),
        // not applied on change - and leaves focus exactly where it was,
        // same as a mouse click does, so Left/Right/Up/Down and repeated
        // toggling keep working unchanged.
        private bool ActivateKitsuCategoryChip()
        {
            int index = GetFocusedKitsuCategoryIndex();

            if (index < 0)
            {
                return false;
            }

            ToggleButton toggle = GetKitsuCategoryContainer(index);

            if (toggle == null)
            {
                return false;
            }

            toggle.IsChecked = !(toggle.IsChecked ?? false);

            return true;
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
        // FOCUS MOVEMENT (Slice 17 rewrite, revised by the Slice 17
        // UI/navigation fixes)
        // =========================================================
        //
        // Top-to-bottom zone chain, each wired bidirectionally to its
        // neighbour, same Up/Down/Left/Right philosophy as every other
        // overlay in this project (CatalogueBrowseView/SearchView):
        //
        //   Header row -> Kitsu filter combos -> Kitsu category chips ->
        //   Apply Filters button -> Kitsu results grid -> Load More
        //
        // (The AnimeFLV grid that used to sit after the Kitsu grid is
        // gone along with the rest of that section - see class remarks.)
        //
        // Filter controls fix: Load More is now wired into this custom
        // Up/Down chain (Down out of the grid's last row when Load More
        // is visible; Up from Load More back to the grid's last card -
        // see IsFocusedKitsuCardInLastRow/the KitsuBrowseLoadMoreButton.
        // IsKeyboardFocused branch below). It previously relied on WPF's
        // default Tab traversal only, which this page's hand-rolled
        // directional navigation never naturally reaches - the fix for
        // "Load More cannot receive focus" via controller/keyboard.
        //
        // Category chips are the one zone that isn't a simple linear
        // step: they wrap across multiple visual rows (WrapPanel), so
        // Up/Down within them has to move a row at a time rather than
        // always exiting the zone - see MoveKitsuCategoryChipsVertical/
        // BuildKitsuCategoryRows below (the fix for the "trapped in
        // categories" issue - Up/Down previously always left the chip
        // area entirely regardless of which row was focused).

        // Filter controls fix: returns the actual focused combo (not just
        // whether one has focus) so ActivateKitsuFilterCombo/
        // MoveKitsuFilterDropDownSelection below can operate on it -
        // mirrors CatalogueBrowseView's own GetFocusedFilterCombo, plus one
        // addition CatalogueBrowseView's copy doesn't need yet: opening a
        // ComboBox's dropdown makes WPF move real keyboard focus from the
        // ComboBox itself onto its currently-selected ComboBoxItem inside
        // the popup (confirmed live - IsKeyboardFocused on the ComboBox
        // goes false, IsKeyboardFocusWithin stays true), so a plain
        // IsKeyboardFocused check alone goes blind the instant the
        // dropdown opens. ActivateKitsuFilterCombo below re-asserts focus
        // on the ComboBox itself right after toggling it open, but that
        // re-focus is deferred a frame (see its remark), so this also
        // accepts IsKeyboardFocusWithin while the dropdown is open as a
        // fallback for the brief window before that runs.
        private ComboBox GetFocusedKitsuFilterCombo()
        {
            return Array.Find(_kitsuFilterCombos, combo =>
                combo != null &&
                (combo.IsKeyboardFocused || (combo.IsDropDownOpen && combo.IsKeyboardFocusWithin)));
        }

        private bool IsFocusInKitsuFilterRow()
        {
            return GetFocusedKitsuFilterCombo() != null;
        }

        private bool IsFocusInKitsuCategoryChips()
        {
            return GetFocusedKitsuCategoryIndex() >= 0;
        }

        private bool IsFocusInKitsuGrid()
        {
            return GetFocusedKitsuCardIndex() >= 0;
        }

        // =========================================================
        // KITSU FILTER COMBO DROPDOWN OPERATION (filter controls fix)
        // =========================================================
        //
        // This view's PreviewKeyDown/HandleControllerInput already claim
        // every Up/Down/Left/Right/Enter/A themselves before a focused
        // child control would ever see them, so a focused combo would
        // never naturally receive an arrow key or Enter/A on its own -
        // same reasoning, and the same solution, as CatalogueBrowseView's
        // own identically-named region: give the filter row its own
        // small, self-contained operation model that drives the ordinary
        // WPF ComboBox (IsDropDownOpen/SelectedIndex) explicitly rather
        // than through WPF's normal input plumbing. Reused verbatim here
        // rather than shared, matching this project's existing per-
        // overlay convention (see FilterComboStyle's own remark in the
        // XAML on why the style itself is duplicated too).

        private bool IsAnyKitsuFilterDropDownOpen()
        {
            return Array.Exists(_kitsuFilterCombos, combo => combo != null && combo.IsDropDownOpen);
        }

        private void CloseAnyKitsuFilterDropDown()
        {
            foreach (ComboBox combo in _kitsuFilterCombos)
            {
                if (combo != null)
                {
                    combo.IsDropDownOpen = false;
                }
            }
        }

        // A/Enter on the filter row: opens the focused combo's dropdown
        // if closed, or confirms (closes) it if already open. Returns
        // false (does nothing) when the filter row doesn't have focus, so
        // callers can fall through to their own activation logic for
        // whatever else is focused - mirrors CatalogueBrowseView's own
        // ActivateFilterCombo. Selection is never applied automatically
        // (AnimeBrowseView has no SelectionChanged handler on any of
        // these combos - unlike Movie/TV, this page's filters only take
        // effect when Apply Filters is pressed), so opening or moving a
        // dropdown's selection can never accidentally trigger Apply
        // Filters.
        //
        // Live-verified fix (not yet needed by CatalogueBrowseView's own
        // copy, so not shared): opening the dropdown makes WPF hand real
        // keyboard focus to the newly-visible ComboBoxItem instead of
        // leaving it on the ComboBox - confirmed by instrumenting this
        // method and watching IsKeyboardFocused flip to false the instant
        // IsDropDownOpen becomes true. Left alone, every subsequent
        // GetFocusedKitsuFilterCombo() call (Up/Down inside the dropdown,
        // and a second Enter to confirm/close it) would find no focused
        // combo and silently no-op, leaving WPF's own native ComboBoxItem
        // key handling to run instead - which tracks its own "highlighted"
        // preview item independently of SelectedIndex, so arrowing through
        // the list looked like it worked but never changed what Apply
        // Filters would actually send. Re-asserting focus on the combo
        // itself, deferred past the layout pass that does the hand-off
        // (same DispatcherPriority.Input convention this file already
        // uses for post-layout focus elsewhere), keeps every later call in
        // this region seeing the right combo as focused in both
        // directions - right after opening, and again after closing,
        // since closing the popup leaves focus sitting on the now-hidden
        // item otherwise.
        private bool ActivateKitsuFilterCombo()
        {
            ComboBox combo = GetFocusedKitsuFilterCombo();

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
        // HandleControllerInput would still intercept first) - mirrors
        // CatalogueBrowseView's own MoveFilterDropDownSelection.
        private void MoveKitsuFilterDropDownSelection(int direction)
        {
            ComboBox combo = GetFocusedKitsuFilterCombo();

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

        private void MoveVertical(int direction)
        {
            // Filter controls fix: while a filter's dropdown is open,
            // Up/Down move its own selection instead of navigating this
            // page - checked first, ahead of every other case below.
            // Checked via IsAnyKitsuFilterDropDownOpen() rather than which
            // control currently has focus, since opening the dropdown
            // itself moves real keyboard focus off the ComboBox (see
            // GetFocusedKitsuFilterCombo/ActivateKitsuFilterCombo's own
            // remarks) - IsDropDownOpen is the state that actually stays
            // reliable for the duration the popup is up. Mirrors
            // CatalogueBrowseView's own MoveVertical.
            if (IsAnyKitsuFilterDropDownOpen())
            {
                MoveKitsuFilterDropDownSelection(direction);
                return;
            }

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

            if (IsFocusInKitsuFilterRow())
            {
                if (direction < 0)
                {
                    CloseButton.Focus();
                    return;
                }

                if (_discoveredCategories.Count > 0)
                {
                    FocusKitsuCategoryToggle(0);
                }
                else
                {
                    KitsuApplyFiltersButton.Focus();
                }

                return;
            }

            if (IsFocusInKitsuCategoryChips())
            {
                MoveKitsuCategoryChipsVertical(direction);
                return;
            }

            if (KitsuApplyFiltersButton.IsKeyboardFocused)
            {
                if (direction < 0)
                {
                    if (_discoveredCategories.Count > 0)
                    {
                        FocusKitsuCategoryToggle(_discoveredCategories.Count - 1);
                    }
                    else
                    {
                        KitsuTypeCombo.Focus();
                    }
                }
                else if (_kitsuBrowseItems.Count > 0)
                {
                    FocusKitsuCard(0);
                }

                return;
            }

            // Load More focus fix: Up from the Load More button returns
            // to the results grid's last card (when there is one) -
            // mirrors the same "clamp at the edge, don't trap" philosophy
            // as every other zone boundary in this chain. Down is a no-op
            // - Load More is the last element in the page.
            if (KitsuBrowseLoadMoreButton.IsKeyboardFocused)
            {
                if (direction < 0 && _kitsuBrowseItems.Count > 0)
                {
                    FocusKitsuCard(_kitsuBrowseItems.Count - 1);
                }

                return;
            }

            if (IsFocusInKitsuGrid())
            {
                if (direction < 0 && GetFocusedKitsuCardIndex() < GetKitsuColumnsPerRow())
                {
                    // Moving up out of the Kitsu grid's first row.
                    KitsuApplyFiltersButton.Focus();
                    return;
                }

                // Load More focus fix: Down out of the grid's last row
                // reaches Load More (when it's actually visible) instead
                // of MoveKitsuGrid silently clamping in place forever -
                // the fix for "Load More cannot receive focus" (previously
                // there was no path into this control at all besides a
                // mouse click or WPF's default Tab traversal).
                if (direction > 0 &&
                    IsFocusedKitsuCardInLastRow() &&
                    KitsuBrowseLoadMoreButton.Visibility == Visibility.Visible)
                {
                    KitsuBrowseLoadMoreButton.Focus();
                    return;
                }

                MoveKitsuGrid(0, direction);
            }
        }

        // Dedicated-page Back navigation task: Down from the Back button
        // needs to reach whichever content actually comes first - the
        // Kitsu filter row is always present, so it is always the
        // landing target.
        private void FocusFirstAvailable()
        {
            KitsuTypeCombo.Focus();
        }

        private void MoveHorizontal(int direction)
        {
            // Filter controls fix: Left/Right while a dropdown is open
            // would otherwise step focus to the next filter with the
            // popup still showing - consumed as a no-op instead, exactly
            // mirroring why Up/Down are repurposed above while a dropdown
            // is open (and CatalogueBrowseView's own identical check).
            if (IsAnyKitsuFilterDropDownOpen())
            {
                return;
            }

            // Persistent hero navigation task: checked first, same as
            // CatalogueBrowseView's own MoveHorizontal - Left/Right while
            // any header-row button has focus cycles the quick-nav
            // buttons and Back, clamping at either end.
            if (IsHeaderRowFocused())
            {
                MoveHeaderRowHorizontal(direction);
                return;
            }

            if (IsFocusInKitsuFilterRow())
            {
                MoveKitsuFilterRowHorizontal(direction);
                return;
            }

            if (IsFocusInKitsuCategoryChips())
            {
                MoveKitsuCategoryChipsHorizontal(direction);
                return;
            }

            if (IsFocusInKitsuGrid())
            {
                MoveKitsuGrid(direction, 0);
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

        // =========================================================
        // KITSU FILTER ROW / CATEGORY CHIPS / RESULTS GRID FOCUS
        // MOVEMENT (Slice 17)
        // =========================================================

        private void MoveKitsuFilterRowHorizontal(int direction)
        {
            int currentIndex =
                Array.FindIndex(
                    _kitsuFilterCombos,
                    combo => combo != null && combo.IsKeyboardFocused);

            if (currentIndex < 0)
            {
                return;
            }

            int target =
                Math.Max(
                    0,
                    Math.Min(currentIndex + direction, _kitsuFilterCombos.Length - 1));

            _kitsuFilterCombos[target]?.Focus();
        }

        // Category chips are variable-width (unlike a fixed-width
        // MediaCard grid), so Left/Right below is a simple flat index
        // +/-1 rather than attempting grid math off an unreliable
        // width/count estimate. Up/Down between wrapped visual rows is
        // handled separately by MoveKitsuCategoryChipsVertical, which
        // reads each chip's real rendered position instead (see that
        // method's own remarks).
        // ItemsControl's default container is a ContentPresenter, not the
        // DataTemplate's own root element directly - same as the
        // MediaCard container lookup elsewhere in this file
        // (GetFocusedKitsuCardIndex), so this needs the same
        // FindVisualChild descent rather than a direct cast.
        private ToggleButton GetKitsuCategoryContainer(int index)
        {
            if (index < 0 || index >= KitsuCategoryItemsControl.Items.Count)
            {
                return null;
            }

            ContentPresenter presenter =
                KitsuCategoryItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            return presenter != null ? FindVisualChild<ToggleButton>(presenter) : null;
        }

        private int GetFocusedKitsuCategoryIndex()
        {
            for (int i = 0; i < KitsuCategoryItemsControl.Items.Count; i++)
            {
                ToggleButton toggle = GetKitsuCategoryContainer(i);

                if (toggle != null && toggle.IsKeyboardFocusWithin)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusKitsuCategoryToggle(int index)
        {
            if (index < 0 || index >= KitsuCategoryItemsControl.Items.Count)
            {
                return;
            }

            ToggleButton toggle = GetKitsuCategoryContainer(index);

            if (toggle == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusKitsuCategoryToggle(index)),
                    DispatcherPriority.Loaded);

                return;
            }

            toggle.Focus();
            toggle.BringIntoView();
        }

        private void MoveKitsuCategoryChipsHorizontal(int direction)
        {
            int current = GetFocusedKitsuCategoryIndex();

            if (current < 0)
            {
                return;
            }

            int target =
                Math.Max(
                    0,
                    Math.Min(current + direction, KitsuCategoryItemsControl.Items.Count - 1));

            FocusKitsuCategoryToggle(target);
        }

        // =========================================================
        // CATEGORY CHIP ROW-AWARE UP/DOWN (UI/navigation fix - the
        // category area previously trapped Up/Down: it always exited
        // the zone entirely regardless of which wrapped row was
        // focused, so a user two or three rows into the chips could
        // never move to the row directly above/below, only jump straight
        // to the filter row or Apply Filters)
        // =========================================================
        //
        // WrapPanel exposes no row/column API, so each chip's row is
        // inferred from its own real rendered Y position relative to the
        // ItemsControl (WrapPanel lays children out left-to-right and
        // only starts a new row at a new Y offset, so chips sharing a Y
        // are, by construction, on the same visual row) - robust to the
        // chips' variable widths, unlike trying to precompute columns
        // from an average width.
        private List<List<int>> BuildKitsuCategoryRows()
        {
            var rows = new List<List<int>>();
            var rowTops = new List<double>();

            int count = KitsuCategoryItemsControl.Items.Count;

            for (int i = 0; i < count; i++)
            {
                ToggleButton toggle = GetKitsuCategoryContainer(i);

                if (toggle == null || !toggle.IsVisible)
                {
                    continue;
                }

                Point topLeft;

                try
                {
                    topLeft = toggle.TransformToAncestor(KitsuCategoryItemsControl).Transform(new Point(0, 0));
                }
                catch (InvalidOperationException)
                {
                    // Not connected to this ancestor yet (mid-layout) -
                    // skip rather than fail the whole navigation move.
                    continue;
                }

                int rowIndex = rowTops.FindIndex(top => Math.Abs(top - topLeft.Y) < 1.0);

                if (rowIndex < 0)
                {
                    rowTops.Add(topLeft.Y);
                    rows.Add(new List<int>());
                    rowIndex = rows.Count - 1;
                }

                rows[rowIndex].Add(i);
            }

            return rows;
        }

        private void MoveKitsuCategoryChipsVertical(int direction)
        {
            int current = GetFocusedKitsuCategoryIndex();

            if (current < 0)
            {
                return;
            }

            List<List<int>> rows = BuildKitsuCategoryRows();
            int currentRow = rows.FindIndex(row => row.Contains(current));

            if (currentRow < 0)
            {
                return;
            }

            int currentColumn = rows[currentRow].IndexOf(current);
            int targetRow = currentRow + direction;

            if (targetRow < 0)
            {
                // Moving up out of the first category row - back to the
                // filter row (matches the old behaviour for this edge).
                KitsuTypeCombo.Focus();
                return;
            }

            if (targetRow >= rows.Count)
            {
                // Moving down out of the last category row - onward to
                // Apply Filters (matches the old behaviour for this edge).
                KitsuApplyFiltersButton.Focus();
                return;
            }

            List<int> targetRowIndices = rows[targetRow];
            int targetColumn = Math.Min(currentColumn, targetRowIndices.Count - 1);

            FocusKitsuCategoryToggle(targetRowIndices[targetColumn]);
        }

        // Same estimated-columns-per-row approach as used for AnimeFLV's
        // own card grid, before that section was removed - valid here
        // because MediaCard (unlike the category chips above) has a
        // fixed width.
        private int GetKitsuColumnsPerRow()
        {
            double width = KitsuBrowseItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            int columns = (int)(width / CardWidth);

            return Math.Max(1, columns);
        }

        // Load More focus fix: true when the focused card is in the
        // grid's last visual row - same index-math shape as
        // CatalogueBrowseView's own IsFocusedFilteredCardInTopRow, just
        // checking the opposite edge (this grid's Down-exit target is
        // Load More, not a zone above it).
        private bool IsFocusedKitsuCardInLastRow()
        {
            int index = GetFocusedKitsuCardIndex();

            if (index < 0)
            {
                return false;
            }

            int count = KitsuBrowseItemsControl.Items.Count;
            int columns = GetKitsuColumnsPerRow();
            int lastRowStart = ((count - 1) / columns) * columns;

            return index >= lastRowStart;
        }

        private void MoveKitsuGrid(int deltaColumns, int deltaRows)
        {
            int count = KitsuBrowseItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedKitsuCardIndex();

            if (current < 0)
            {
                FocusKitsuCard(0);
                return;
            }

            int columns = GetKitsuColumnsPerRow();
            int target = current + deltaColumns + (deltaRows * columns);

            target = Math.Max(0, Math.Min(target, count - 1));

            FocusKitsuCard(target);
        }

        private int GetFocusedKitsuCardIndex()
        {
            for (int i = 0; i < KitsuBrowseItemsControl.Items.Count; i++)
            {
                ContentPresenter presenter =
                    KitsuBrowseItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

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

        private void FocusKitsuCard(int index)
        {
            if (index < 0 || index >= KitsuBrowseItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                KitsuBrowseItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusKitsuCard(index)),
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
