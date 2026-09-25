using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Events;
using StremioVideoCenterControls;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenter.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenter
{
    public partial class StremioVideoCenterView : PluginUserControl
    {
        // =========================================================
        // MEDIA SECTIONS
        // =========================================================

        private readonly MediaSection[] sections;

        // =========================================================
        // TOP NAV BUTTONS (navigation stabilization pass)
        // =========================================================
        //
        // Ordered left-to-right exactly as they appear in the nav bar
        // StackPanel in StremioVideoCenterView.xaml. Single source of
        // truth for top-nav Left/Right/Up/Down navigation (see
        // HandleTopNavDirectional) so HOME/MOVIES/TV SHOWS/etc. never
        // again depend on WPF's default directional-navigation fallback.

        private readonly Button[] _topNavButtons;

        // =========================================================
        // STREMIO PLAYBACK TEST
        // =========================================================

        private MpvPlayer _stremioPlayer;

        // =========================================================
        // SOURCE SELECTION (Slice 9)
        // =========================================================
        //
        // Non-null only while a SourceSelectorView is actively shown for
        // one in-progress selection. Created fresh per selection (unlike
        // _stremioPlayer, which is reused across a whole viewing session)
        // and torn down immediately after SourceSelected/SelectionCancelled.

        private SourceSelectorView _activeSourceSelector;

        // =========================================================
        // EPISODE BROWSER (Slice 12)
        // =========================================================
        //
        // Non-null only while an EpisodeBrowserView is actively shown for
        // one in-progress TV show selection. Same lifecycle discipline as
        // _activeSourceSelector: created fresh, torn down immediately
        // after EpisodeSelected/BrowsingCancelled.

        private EpisodeBrowserView _activeEpisodeBrowser;

        // =========================================================
        // MEDIA SELECTION IN-FLIGHT GUARD (navigation stabilization pass)
        // =========================================================
        //
        // Set for the duration of HandleMediaSelectedAsync - from the
        // moment a card/episode is selected until playback either starts
        // or the flow is cancelled/fails. Without this, a second
        // selection made while the first was still resolving (e.g. while
        // waiting on ShowEpisodeBrowserAsync's TV -> episode browsing or
        // PlaySelectedMediaAsync's source aggregation) could race the
        // first: both could end up manipulating _activeEpisodeBrowser/
        // _activeSourceSelector and the overlay hosts concurrently,
        // which was the likely cause of the Source Selector occasionally
        // showing for the wrong item after fast repeated selections. A
        // selection made while one is already in flight is simply
        // ignored rather than queued or cancelled - there is no loading
        // indicator today, so silently dropping the extra input is the
        // smallest safe behaviour.

        private bool _isResolvingMediaSelection;

        // =========================================================
        // LOCAL MEDIA (Cinemeta-First Addon Settings Architecture
        // slice addendum - navigation foundation only)
        // =========================================================
        //
        // Non-null only while a LocalMediaView is actively shown. Same
        // lifecycle discipline as _activeEpisodeBrowser/
        // _activeSourceSelector: created fresh, torn down immediately
        // when the user closes it.

        private LocalMediaView _activeLocalMediaView;

        // =========================================================
        // MOVIES / TV / ANIME / SEARCH PAGES (Slice 15)
        // =========================================================
        //
        // Same lifecycle discipline as _activeLocalMediaView/
        // _activeEpisodeBrowser/_activeSourceSelector above: created
        // fresh per visit, torn down immediately when the user closes it.
        // Only one of these (plus the other overlays above) is ever
        // non-null at a time - see HandleControllerInput's "most specific
        // overlay wins" chain.

        private CatalogueBrowseView _activeMoviesPage;
        private CatalogueBrowseView _activeTvPage;
        private AnimeBrowseView _activeAnimePage;
        private SearchView _activeSearchPage;

        // =========================================================
        // LOCAL MEDIA LIBRARY LOCATIONS (Slice 15 - architecture only)
        // =========================================================

        private readonly List<LibraryLocation> _libraryLocations;

        // =========================================================
        // STARTUP STATE
        // =========================================================

        private bool _startupSequenceStarted;


        // =========================================================
        // TMDB CREDENTIAL
        // =========================================================

        private readonly string _tmdbAccessToken;


        // =========================================================
        // OMDB CREDENTIAL
        // =========================================================

        private readonly string _omdbApiKey;


        // =========================================================
        // REAL-DEBRID CREDENTIAL
        // =========================================================

        private readonly string _realDebridApiToken;


        // =========================================================
        // STREMIO ADDON URLS (newline-delimited, legacy)
        // =========================================================

        private readonly string _stremioAddonUrls;


        // =========================================================
        // STREMIO ADDON INSTANCES (structured)
        // =========================================================

        private readonly List<AddonInstance> _addonInstances;


        // =========================================================
        // HERO ROTATION LIFECYCLE (lifecycle fix)
        // =========================================================
        //
        // Owns the lifetime of the hero auto-rotation loop (Start
        // HeroBackdropRotationAsync/ChangeHero/LoadHeroAndRestartRotation
        // Async below). Non-null only while a rotation chain is actually
        // running for THIS view instance - created once in
        // StremioVideoCenterView_Loaded, cancelled and cleared by
        // StopHeroBackdropRotation (called from Unloaded as a backstop,
        // and expected to also be called explicitly by whatever owns this
        // view's window before dropping its reference). Every leg of the
        // rotation chain checks this token before doing any further work,
        // so cancelling it reliably stops the loop rather than merely
        // interrupting the current delay.

        private CancellationTokenSource _heroRotationCts;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterView(
            StremioVideoCenterConfiguration configuration = null)
        {
            InitializeComponent();

            configuration = configuration ?? new StremioVideoCenterConfiguration();

            _tmdbAccessToken =
                configuration.TmdbApiKey;

            _omdbApiKey =
                configuration.OmdbApiKey;

            _realDebridApiToken =
                configuration.RealDebridApiKey;

            _stremioAddonUrls =
                configuration.StremioAddonUrls;

            _addonInstances =
                configuration.AddonInstances;

            _libraryLocations =
                configuration.LibraryLocations;

            DataContext =
                new HomeViewModel();

            // Slice 15 Home restructure: MyList/ContinueWatching first
            // (matching the required conditional-section order), then the
            // six Featured/Popular rows - replaces the old twelve-section
            // array (nine Top10/Trending/RecentlyReleased rows plus the
            // unrelated, never-wired "Recently Added" row).
            sections =
                new[]
                {
                    MyListSection,
                    ContinueWatchingSection,
                    FeaturedMoviesSection,
                    PopularMoviesSection,
                    FeaturedShowsSection,
                    PopularShowsSection,
                    FeaturedAnimeSection,
                    PopularAnimeSection
                };

            _topNavButtons =
                new[]
                {
                    HomeButton,
                    MoviesButton,
                    TvShowsButton,
                    AnimeButton,
                    LocalMediaButton,
                    NavigationMyListButton,
                    HistoryButton,
                    SearchButton
                };

            foreach (MediaSection section in sections)
            {
                if (section != null)
                {
                    section.MediaSelected +=
                        MediaSection_MediaSelected;
                }
            }

            // =========================================================
            // "SEE MORE" LINKS (Slice 15)
            // =========================================================
            //
            // One link per Featured/Popular row, each navigating to the
            // matching dedicated page - real navigation now, replacing
            // Slice 13's documented MessageBox placeholders. MyList/
            // ContinueWatching intentionally get no "See More" link this
            // slice - no dedicated page exists for either yet (Slice
            // 16/17), and this slice's own instructions are explicit:
            // "do not invent one just to satisfy the button."

            FeaturedMoviesSection.MoreLabel = "See More →";
            FeaturedMoviesSection.MoreButtonVisibility = Visibility.Visible;
            FeaturedMoviesSection.MoreClicked += MoreMovies_Clicked;

            PopularMoviesSection.MoreLabel = "See More →";
            PopularMoviesSection.MoreButtonVisibility = Visibility.Visible;
            PopularMoviesSection.MoreClicked += MoreMovies_Clicked;

            FeaturedShowsSection.MoreLabel = "See More →";
            FeaturedShowsSection.MoreButtonVisibility = Visibility.Visible;
            FeaturedShowsSection.MoreClicked += MoreTvShows_Clicked;

            PopularShowsSection.MoreLabel = "See More →";
            PopularShowsSection.MoreButtonVisibility = Visibility.Visible;
            PopularShowsSection.MoreClicked += MoreTvShows_Clicked;

            FeaturedAnimeSection.MoreLabel = "See More →";
            FeaturedAnimeSection.MoreButtonVisibility = Visibility.Visible;
            FeaturedAnimeSection.MoreClicked += MoreAnime_Clicked;

            PopularAnimeSection.MoreLabel = "See More →";
            PopularAnimeSection.MoreButtonVisibility = Visibility.Visible;
            PopularAnimeSection.MoreClicked += MoreAnime_Clicked;

            Loaded +=
                StremioVideoCenterView_Loaded;

            // Backstop for the hero rotation lifecycle fix: this view is
            // also handed out directly by StremioVideoCenter.GetGameViewControl
            // (the "VideoCenter" custom element path), which has no window-
            // close event of its own to hook - Unloaded fires whenever WPF
            // disconnects this control from a rendered visual tree either
            // way, so it's a safe place to guarantee the rotation loop
            // stops even if whatever hosts this view never calls
            // StopHeroBackdropRotation() itself. Calling it twice (here and
            // from an explicit external call) is harmless - see its own
            // remarks.
            Unloaded +=
                StremioVideoCenterView_Unloaded;

            PreviewKeyDown +=
                StremioVideoCenterView_PreviewKeyDown;

            PreviewMouseWheel +=
                StremioVideoCenterView_PreviewMouseWheel;
        }


        // =========================================================
        // INITIAL LOAD
        // =========================================================

        private async void StremioVideoCenterView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            if (_startupSequenceStarted)
            {
                return;
            }

            _startupSequenceStarted = true;

            if (DataContext is HomeViewModel vm)
            {
                // Cinemeta-First Addon Settings Architecture: TMDB is
                // now OPTIONAL (Part 12). tmdbService is null when no
                // API key is configured - Hero (TMDB-only, unchanged)
                // simply keeps its fallback data in that case, while the
                // catalogue always loads (CatalogueService falls back to
                // Cinemeta - which needs no credential - when
                // tmdbService is null; see CatalogueService's own class
                // remarks). Hero and the catalogue remain independent
                // consumers running concurrently, each still failing
                // safe on its own.
                TmdbService tmdbService =
                    !string.IsNullOrWhiteSpace(_tmdbAccessToken)
                        ? new TmdbService(_tmdbAccessToken)
                        : null;

                Task heroTask =
                    tmdbService != null
                        ? LoadHeroSafeAsync(vm, tmdbService)
                        : Task.CompletedTask;

                Task catalogueTask =
                    LoadCatalogueSafeAsync(vm, tmdbService);

                await Task.WhenAll(
                    heroTask,
                    catalogueTask);
            }

            UpdateHeroIndicators();

            await Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FocusDefaultHomeTarget();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // Lifecycle fix: if the view was already unloaded (window
            // closed/B pressed) before this async continuation got back
            // here, do not start a rotation loop that nothing would ever
            // stop - StopHeroBackdropRotation only cancels a token that
            // already exists, it can't retroactively stop one created
            // after it ran.
            if (!IsLoaded)
            {
                return;
            }

            _heroRotationCts =
                new CancellationTokenSource();

            await StartHeroBackdropRotationAsync(
                _heroRotationCts.Token);
        }


        // =========================================================
        // LOAD HERO (fail-safe)
        // =========================================================

        private async Task LoadHeroSafeAsync(
            HomeViewModel vm,
            TmdbService tmdbService)
        {
            try
            {
                HeroRecommendationService recommendationService =
                    new HeroRecommendationService(
                        tmdbService);

                await vm.LoadHeroRecommendationsAsync(
                    recommendationService);
            }
            catch
            {
                // HomeViewModel retains its fallback Hero data.
            }
        }

        // =========================================================
        // LOAD CATALOGUE (fail-safe)
        // =========================================================
        //
        // Real Catalogue & Content Foundation: replaces the hardcoded
        // Top 10 rows and populates the new Trending/Recently Released
        // rows with real content. Cinemeta-First Addon Settings
        // Architecture: tmdbService may be null (no TMDB key
        // configured) - CatalogueService itself falls back to Cinemeta
        // per row in that case, so this always runs, unlike Hero.

        private async Task LoadCatalogueSafeAsync(
            HomeViewModel vm,
            TmdbService tmdbService)
        {
            try
            {
                CinemetaService cinemetaService =
                    new CinemetaService();

                CatalogueService catalogueService =
                    new CatalogueService(tmdbService, cinemetaService);

                await vm.LoadCatalogueAsync(
                    catalogueService);
            }
            catch
            {
                // HomeViewModel retains its fallback/test catalogue
                // data for any row that didn't load.
            }
        }

        // =========================================================
        // LOAD HERO BACKDROP
        // =========================================================

        private async Task LoadCurrentHeroBackdropAsync(
            HeroItem heroItem)
        {
            if (heroItem == null)
            {
                return;
            }

            try
            {
                await heroItem.LoadBackdropAsync();
            }
            catch
            {
                // The bound BackdropUrl remains the fallback source.
            }
        }


        // =========================================================
        // HERO BACKDROP ROTATION
        // =========================================================
        //
        // Lifecycle fix: this method used to be a plain fire-and-forget
        // 15-second delay with no way to stop it - every launch of Zino
        // left one more of these running forever, each still holding this
        // whole view/HomeViewModel graph alive. It now takes the
        // CancellationToken created once per view instance in
        // StremioVideoCenterView_Loaded (see _heroRotationCts) and checks
        // it both before and after the delay, so a cancelled token stops
        // the loop outright rather than merely being interrupted and
        // immediately restarted by whatever called this. Existing
        // behaviour (15-second interval, same-item skip check, same
        // ChangeHero(1) call) is otherwise unchanged.

        private async Task StartHeroBackdropRotationAsync(
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            HomeViewModel vm =
                DataContext as HomeViewModel;

            if (vm == null ||
                vm.CurrentHeroItem == null)
            {
                return;
            }

            HeroItem heroItem =
                vm.CurrentHeroItem;

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Zino closed while waiting for the next rotation - this
                // is the expected, quiet way the loop stops, not an error.
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (ReferenceEquals(
                    vm.CurrentHeroItem,
                    heroItem))
            {
                ChangeHero(1);
            }
        }


        // =========================================================
        // LOAD HERO AND RESTART ROTATION
        // =========================================================

        private async Task LoadHeroAndRestartRotationAsync(
            HeroItem heroItem,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await LoadCurrentHeroBackdropAsync(
                heroItem);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await StartHeroBackdropRotationAsync(
                cancellationToken);
        }


        // =========================================================
        // RESTART HERO ROTATION FOR (lifecycle fix)
        // =========================================================
        //
        // Single entry point ChangeHero/SetHeroIndex use to (re)kick the
        // rotation loop after a manual hero change. Reads _heroRotationCts
        // fresh on every call (rather than capturing it once) since this
        // can be invoked long after Loaded, and refuses to schedule new
        // work once it's null/cancelled - this is what stops
        // ChangeHero -> LoadHeroAndRestartRotationAsync -> StartHeroBackdrop
        // RotationAsync from restarting the loop after
        // StopHeroBackdropRotation has run, instead of only cancelling
        // whichever single Task.Delay happened to be in flight.

        private void RestartHeroRotationFor(
            HeroItem heroItem)
        {
            CancellationTokenSource cts =
                _heroRotationCts;

            if (cts == null ||
                cts.IsCancellationRequested)
            {
                return;
            }

            _ =
                LoadHeroAndRestartRotationAsync(
                    heroItem,
                    cts.Token);
        }


        // =========================================================
        // STOP HERO ROTATION (lifecycle fix)
        // =========================================================
        //
        // Cancels and releases the rotation loop's CancellationTokenSource,
        // if one exists. Safe to call multiple times, before the view ever
        // finished loading, after it's already been stopped, or while the
        // loop is mid-delay/mid-network-call - every leg of the chain
        // above checks the token before doing further work, so this is
        // enough to guarantee no further rotation happens for this view
        // instance once it returns. Called explicitly by whatever owns
        // this view's window before dropping its reference (see
        // StremioVideoCenter.VideoCenterWindow_Closed), and again from
        // this view's own Unloaded handler as a backstop.

        public void StopHeroBackdropRotation()
        {
            CancellationTokenSource cts =
                _heroRotationCts;

            _heroRotationCts = null;

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier call - nothing left to
                // cancel.
            }
            finally
            {
                cts.Dispose();
            }
        }


        // =========================================================
        // UNLOADED (lifecycle fix backstop)
        // =========================================================

        private void StremioVideoCenterView_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            StopHeroBackdropRotation();
        }


        // =========================================================
        // CHANGE HERO
        // =========================================================

        private void ChangeHero(
            int direction)
        {
            HomeViewModel vm =
                DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            if (vm.HeroItems == null ||
                vm.HeroItems.Count == 0)
            {
                return;
            }

            int nextIndex =
                vm.CurrentHeroIndex +
                direction;

            if (nextIndex < 0)
            {
                nextIndex =
                    vm.HeroItems.Count - 1;
            }

            if (nextIndex >= vm.HeroItems.Count)
            {
                nextIndex = 0;
            }

            vm.CurrentHeroIndex =
                nextIndex;

            UpdateHeroIndicators();

            if (vm.CurrentHeroItem != null)
            {
                RestartHeroRotationFor(
                    vm.CurrentHeroItem);
            }
        }


        // =========================================================
        // SET HERO INDEX
        // =========================================================

        private void SetHeroIndex(
            int index)
        {
            HomeViewModel vm =
                DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            if (vm.HeroItems == null ||
                index < 0 ||
                index >= vm.HeroItems.Count)
            {
                return;
            }

            vm.CurrentHeroIndex =
                index;

            UpdateHeroIndicators();

            if (vm.CurrentHeroItem != null)
            {
                RestartHeroRotationFor(
                    vm.CurrentHeroItem);
            }
        }


        // =========================================================
        // HERO INDICATORS
        // =========================================================

        private void UpdateHeroIndicators()
        {
            HomeViewModel vm =
                DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            int count =
                vm.HeroItems != null
                    ? vm.HeroItems.Count
                    : 0;

            int currentIndex =
                vm.CurrentHeroIndex;

            UpdateHeroIndicator(
                HeroIndicator1,
                HeroIndicatorEllipse1,
                0,
                count,
                currentIndex);

            UpdateHeroIndicator(
                HeroIndicator2,
                HeroIndicatorEllipse2,
                1,
                count,
                currentIndex);

            UpdateHeroIndicator(
                HeroIndicator3,
                HeroIndicatorEllipse3,
                2,
                count,
                currentIndex);

            UpdateHeroIndicator(
                HeroIndicator4,
                HeroIndicatorEllipse4,
                3,
                count,
                currentIndex);

            UpdateHeroIndicator(
                HeroIndicator5,
                HeroIndicatorEllipse5,
                4,
                count,
                currentIndex);
        }


        // =========================================================
        // UPDATE INDIVIDUAL INDICATOR
        // =========================================================

        private void UpdateHeroIndicator(
            System.Windows.Controls.Button button,
            System.Windows.Shapes.Ellipse ellipse,
            int index,
            int heroCount,
            int currentIndex)
        {
            if (index >= heroCount)
            {
                button.Visibility =
                    Visibility.Collapsed;

                return;
            }

            button.Visibility =
                Visibility.Visible;

            if (index == currentIndex)
            {
                ellipse.Fill =
                    new SolidColorBrush(
                        Color.FromRgb(
                            255,
                            42,
                            120));

                ellipse.Width = 11;
                ellipse.Height = 11;
            }
            else
            {
                ellipse.Fill =
                    new SolidColorBrush(
                        Color.FromRgb(
                            119,
                            119,
                            127));

                ellipse.Width = 9;
                ellipse.Height = 9;
            }
        }


        // =========================================================
        // HERO ARROW BUTTONS
        // =========================================================

        private void HeroPreviousButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ChangeHero(-1);

            HeroPreviousButton.Focus();
        }


        private void HeroNextButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ChangeHero(1);

            HeroNextButton.Focus();
        }


        // =========================================================
        // HERO INDICATOR BUTTONS
        // =========================================================

        private void HeroIndicator1_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetHeroIndex(0);
        }


        private void HeroIndicator2_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetHeroIndex(1);
        }


        private void HeroIndicator3_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetHeroIndex(2);
        }


        private void HeroIndicator4_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetHeroIndex(3);
        }


        private void HeroIndicator5_Click(
            object sender,
            RoutedEventArgs e)
        {
            SetHeroIndex(4);
        }


        // =========================================================
        // DEFAULT FOCUS (Home load)
        // =========================================================
        //
        // Lifecycle fix: Home's default focus now lands on the existing
        // HomeButton (Hero navigation menu), a real, always-visible,
        // IsTabStop Button - not the first media row. FocusFirstAvailable
        // Section() below is kept only as a fallback for the case where
        // HomeButton itself cannot take focus; it is not expected to run
        // in normal operation any more, but is left in place rather than
        // removed since it is still the mechanism the other "return to
        // Home" call sites throughout this file use directly.

        private void FocusDefaultHomeTarget()
        {
            bool focused =
                HomeButton != null &&
                HomeButton.Focus();

            if (!focused)
            {
                FocusFirstAvailableSection();
            }
        }


        // =========================================================
        // FOCUS FIRST AVAILABLE SECTION
        // =========================================================
        //
        // Lifecycle fix: previously only checked section.Items != null,
        // which an empty-but-initialized ObservableCollection (MyList/
        // ContinueWatching before Slice 16/17 ever populate them) always
        // satisfies - selecting such a section drove MediaSection.
        // FocusCard(0) into what was an unbounded retry loop (see that
        // method's own remarks; it is now bounded/self-terminating
        // regardless, but this check avoids ever relying on that as the
        // only safeguard). section.HasItems checks the section's real,
        // realized item count instead, so only a section that genuinely
        // has at least one focusable card can be selected here.

        private void FocusFirstAvailableSection()
        {
            List<MediaSection> populatedSections =
                GetPopulatedSections();

            if (populatedSections.Count == 0)
            {
                return;
            }

            populatedSections[0].FocusFirstCard();

            MainScrollViewer.ScrollToVerticalOffset(0);
        }


        // =========================================================
        // POPULATED SECTIONS (navigation stabilization pass)
        // =========================================================
        //
        // Single, shared, ordered list of the Home sections that actually
        // have at least one realized item to focus - the one source of
        // truth every navigation path (initial focus, Hero -> Down, and
        // MoveVertical's Up/Down between rows) now uses instead of each
        // maintaining its own "is this section empty" check. Preserves
        // the existing sections[] order. An empty section (HasItems
        // false - e.g. MyList/ContinueWatching before Slice 16/17 ever
        // populate them, or Popular Anime today) is excluded here so it
        // can never consume a navigation step or silently swallow one.

        private List<MediaSection> GetPopulatedSections()
        {
            List<MediaSection> populated =
                new List<MediaSection>();

            foreach (MediaSection section in sections)
            {
                if (section != null &&
                    section.HasItems)
                {
                    populated.Add(section);
                }
            }

            return populated;
        }


        // =========================================================
        // KEYBOARD / CONTROLLER NAVIGATION
        // =========================================================
        //
        // Navigation stabilization pass: keyboard and controller Home
        // navigation used to be two independently hand-maintained copies
        // of the same left/right/up/down state machine (this handler and
        // HandleHomeDirectionalNavigation below, called from
        // HandleControllerInput). They had already started to matter for
        // why keyboard and controller could behave differently - any
        // future edit to one and not the other silently diverges the two.
        // Keyboard now delegates to the single shared implementation the
        // controller path already used, so there is exactly one place
        // that defines Home's directional navigation.

        private void StremioVideoCenterView_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            // Runtime regression fix: SelectFocusedMediaCard() already
            // existed (reused below) and was already wired to the
            // controller's A button, but nothing ever called it for a
            // keyboard Enter/Space press - MediaCard's own PreviewKeyDown
            // only fires when the MediaCard itself is literally the
            // focused element, not when a focused-within descendant or a
            // nav/Hero Button holds focus. Routing keyboard Enter/Space
            // through the same SelectFocusedMediaCard() the controller
            // already uses reuses the existing activation path instead of
            // introducing a second one.
            if (e.Key == Key.Enter ||
                e.Key == Key.Space)
            {
                if (SelectFocusedMediaCard())
                {
                    e.Handled = true;
                }

                return;
            }

            if (e.Key != Key.Left &&
                e.Key != Key.Right &&
                e.Key != Key.Up &&
                e.Key != Key.Down)
            {
                return;
            }

            if (HandleHomeDirectionalNavigation(e.Key))
            {
                e.Handled = true;
            }
        }


        // =========================================================
        // VERTICAL NAVIGATION
        // =========================================================

        // Navigation stabilization pass: previously walked the raw
        // sections[] array and only rejected a target whose Items was
        // null - a bound ObservableCollection is non-null even when
        // genuinely empty (MyList/ContinueWatching before Slice 16/17
        // populate them, Popular Anime today), so landing on one of
        // those called FocusFirstCard() on an empty section, which
        // silently did nothing (no container at index 0). The result
        // looked like Up/Down got "stuck" on the previous row, and a
        // second press then jumped past the empty row into the next
        // populated one - the confirmed "skips Popular TV Shows" symptom.
        // This now walks GetPopulatedSections() instead, so an empty
        // section is never a possible target and can never consume a
        // navigation step. It also preserves the focused column position
        // across rows where practical, falling back to the first card
        // when the target row has fewer items.
        private void MoveVertical(
            MediaSection currentSection,
            Key key)
        {
            List<MediaSection> populatedSections =
                GetPopulatedSections();

            int currentIndex =
                populatedSections.IndexOf(
                    currentSection);

            if (currentIndex < 0)
            {
                return;
            }

            if (key == Key.Up &&
                currentIndex == 0)
            {
                HeroWatchNowButton.Focus();

                return;
            }

            int direction =
                key == Key.Up
                    ? -1
                    : 1;

            int targetIndex =
                currentIndex + direction;

            if (targetIndex < 0 ||
                targetIndex >= populatedSections.Count)
            {
                return;
            }

            MediaSection targetSection =
                populatedSections[targetIndex];

            int columnIndex =
                currentSection.GetFocusedIndex();

            if (columnIndex < 0)
            {
                columnIndex = 0;
            }

            targetSection.FocusCardAt(columnIndex);

            if (!targetSection.IsFirstCardVisible(
                    MainScrollViewer))
            {
                MainScrollViewer.ScrollToVerticalOffset(
                    GetSectionOffset(
                        targetSection));
            }
        }


        // =========================================================
        // FIND CURRENT SECTION
        // =========================================================

        private MediaSection GetFocusedSection()
        {
            foreach (MediaSection section in sections)
            {
                if (section != null &&
                    section.IsKeyboardFocusWithin)
                {
                    return section;
                }
            }

            return null;
        }


        // =========================================================
        // MOUSE WHEEL
        // =========================================================

        private void StremioVideoCenterView_PreviewMouseWheel(
            object sender,
            MouseWheelEventArgs e)
        {
            if (MainScrollViewer == null)
            {
                return;
            }

            double amount =
                e.Delta * 0.8;

            double targetOffset =
                MainScrollViewer.VerticalOffset
                - amount;

            double maxOffset =
                Math.Max(
                    0,
                    MainScrollViewer.ExtentHeight
                    - MainScrollViewer.ViewportHeight);

            targetOffset =
                Math.Max(
                    0,
                    Math.Min(
                        targetOffset,
                        maxOffset));

            MainScrollViewer.ScrollToVerticalOffset(
                targetOffset);

            e.Handled = true;
        }


        // =========================================================
        // SECTION SCROLL POSITION
        // =========================================================

        private double GetSectionOffset(
            MediaSection section)
        {
            try
            {
                GeneralTransform transform =
                    section.TransformToAncestor(
                        MainScrollViewer);

                Point point =
                    transform.Transform(
                        new Point(0, 0));

                double targetOffset =
                    MainScrollViewer.VerticalOffset
                    + point.Y
                    - 30;

                double maxOffset =
                    Math.Max(
                        0,
                        MainScrollViewer.ExtentHeight
                        - MainScrollViewer.ViewportHeight);

                return Math.Max(
                    0,
                    Math.Min(
                        targetOffset,
                        maxOffset));
            }
            catch
            {
                return MainScrollViewer.VerticalOffset;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            // Episode browser owns controller input while season/episode
            // selection is in progress - checked first, since it's the
            // "most specific" overlay when present (mirrors how the source
            // selector is already checked before the player).
            if (_activeEpisodeBrowser != null)
            {
                return _activeEpisodeBrowser.HandleControllerInput(button);
            }

            // Source selector owns controller input while a selection is
            // in progress - checked before the player, since a selector and
            // a player are never active at the same time, but checking
            // selector first is harmless either way and keeps this
            // consistent with "most specific overlay wins" ordering.
            if (_activeSourceSelector != null)
            {
                return _activeSourceSelector.HandleControllerInput(button);
            }

            // Local Media owns controller input while its overlay is open
            // - same "most specific overlay wins" ordering as the other
            // three above.
            if (_activeLocalMediaView != null)
            {
                return _activeLocalMediaView.HandleControllerInput(button);
            }

            // Movies/TV/Anime/Search pages (Slice 15) - same "most
            // specific overlay wins" ordering. Only one of these is ever
            // non-null at a time (each Show*PageAsync method is awaited
            // to completion before another can open).
            if (_activeMoviesPage != null)
            {
                return _activeMoviesPage.HandleControllerInput(button);
            }

            if (_activeTvPage != null)
            {
                return _activeTvPage.HandleControllerInput(button);
            }

            if (_activeAnimePage != null)
            {
                return _activeAnimePage.HandleControllerInput(button);
            }

            if (_activeSearchPage != null)
            {
                return _activeSearchPage.HandleControllerInput(button);
            }

            // Player owns controller input while playback is active.
            if (_stremioPlayer != null)
            {
                return _stremioPlayer.HandleControllerInput(button);
            }

            switch (button)
            {
                case ControllerInput.A:
                    return SelectFocusedMediaCard();

                case ControllerInput.DPadLeft:
                    return HandleHomeDirectionalNavigation(Key.Left);

                case ControllerInput.DPadRight:
                    return HandleHomeDirectionalNavigation(Key.Right);

                case ControllerInput.DPadUp:
                    return HandleHomeDirectionalNavigation(Key.Up);

                case ControllerInput.DPadDown:
                    return HandleHomeDirectionalNavigation(Key.Down);

                default:
                    return false;
            }
        }

        // Navigation stabilization pass: Controller A previously only
        // activated a focused MediaCard - pressing A while focus was on
        // a nav bar button or a Hero button (Watch Now, My List,
        // Previous/Next) did nothing, since GetFocusedSection() only
        // looks inside the content sections. Keyboard Enter/Space and a
        // real mouse click already activate any focused Button via
        // WPF's own built-in Click handling, so re-raising that same
        // Click event here reuses the existing click/selection path
        // (every Click="..." handler already wired in XAML) instead of
        // introducing a second activation system.
        private bool SelectFocusedMediaCard()
        {
            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(
                    new RoutedEventArgs(
                        ButtonBase.ClickEvent));

                return true;
            }

            MediaSection currentSection = GetFocusedSection();

            if (currentSection == null)
            {
                return false;
            }

            MediaCard focusedCard =
                FindFocusedMediaCard(currentSection);

            if (focusedCard == null)
            {
                currentSection.FocusFirstCard();
                return true;
            }

            focusedCard.RaiseEvent(
                new RoutedEventArgs(
                    MediaCard.SelectedEvent));

            return true;
        }

        private MediaCard FindFocusedMediaCard(
            DependencyObject parent)
        {
            if (parent == null)
            {
                return null;
            }

            MediaCard card = parent as MediaCard;

            if (card != null &&
                card.IsKeyboardFocusWithin)
            {
                return card;
            }

            int childCount =
                VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child =
                    VisualTreeHelper.GetChild(parent, i);

                MediaCard result =
                    FindFocusedMediaCard(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        // Single shared implementation of Home's directional navigation -
        // see the remarks on StremioVideoCenterView_PreviewKeyDown above
        // for why keyboard now delegates here instead of duplicating this
        // logic. "Controller" in the name is kept only because this was
        // the controller-side implementation before the consolidation;
        // it is no longer controller-specific.
        private bool HandleHomeDirectionalNavigation(
            Key key)
        {
            if (HeroPreviousButton.IsKeyboardFocusWithin ||
                HeroNextButton.IsKeyboardFocusWithin)
            {
                if (key == Key.Left)
                {
                    ChangeHero(-1);
                    HeroPreviousButton.Focus();
                    return true;
                }

                if (key == Key.Right)
                {
                    ChangeHero(1);
                    HeroNextButton.Focus();
                    return true;
                }

                if (key == Key.Down)
                {
                    // Navigation-hierarchy correction: the Hero control
                    // layer sits directly above Home's content rows -
                    // Down from here goes straight to the first card of
                    // the first POPULATED row (mirrors the identical
                    // populated-section lookup the Watch Now/My List
                    // block below already uses), not through Watch Now/
                    // My List as an intermediate stop.
                    List<MediaSection> populatedSections =
                        GetPopulatedSections();

                    if (populatedSections.Count > 0)
                    {
                        populatedSections[0].FocusFirstCard();
                    }

                    return true;
                }

                if (key == Key.Up)
                {
                    HomeButton.Focus();
                    return true;
                }
            }

            if (HeroWatchNowButton.IsKeyboardFocusWithin ||
                HeroMyListButton.IsKeyboardFocusWithin)
            {
                if (key == Key.Left)
                {
                    if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        HeroWatchNowButton.Focus();
                    }
                    else
                    {
                        SearchButton.Focus();
                    }

                    return true;
                }

                if (key == Key.Right)
                {
                    if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        HeroMyListButton.Focus();
                    }
                    else
                    {
                        HomeButton.Focus();
                    }

                    return true;
                }

                if (key == Key.Up)
                {
                    HeroPreviousButton.Focus();
                    return true;
                }

                if (key == Key.Down)
                {
                    // Navigation stabilization pass: was sections[0]
                    // directly - if MyList/ContinueWatching (or any
                    // earlier row) is present but empty, index 0 is not
                    // actually focusable and this silently did nothing.
                    // First POPULATED section is the correct target per
                    // the intended Home design.
                    List<MediaSection> populatedSections =
                        GetPopulatedSections();

                    if (populatedSections.Count > 0)
                    {
                        populatedSections[0].FocusFirstCard();
                    }

                    return true;
                }
            }

            // Navigation stabilization pass: top-nav Left/Right/Up/Down is
            // now owned entirely by HandleTopNavDirectional (single
            // shared, ordered implementation) instead of the three
            // separate ad-hoc checks this replaced (a Down check
            // enumerating every nav button by name, plus one-off
            // SearchButton+Right and HomeButton+Left special cases). This
            // is also what stops HOME + Right (and Up) from ever falling
            // through to WPF's default directional-navigation traversal
            // again - every direction is now explicitly claimed while any
            // top-nav button has focus.
            if (HandleTopNavDirectional(key))
            {
                return true;
            }

            MediaSection currentSection =
                GetFocusedSection();

            if (key == Key.Left ||
                key == Key.Right)
            {
                if (currentSection == null)
                {
                    return false;
                }

                currentSection.MoveHorizontal(
                    key == Key.Left ? -1 : 1);

                return true;
            }

            if (key == Key.Up ||
                key == Key.Down)
            {
                if (currentSection == null)
                {
                    return false;
                }

                MoveVertical(
                    currentSection,
                    key);

                return true;
            }

            return false;
        }


        // =========================================================
        // TOP NAV DIRECTIONAL NAVIGATION (navigation stabilization pass)
        // =========================================================
        //
        // Single, shared, deterministic implementation of Left/Right/Up/
        // Down while a top-nav button (HOME/MOVIES/TV SHOWS/ANIME/LOCAL
        // MEDIA/MY LIST/HISTORY/SEARCH) has focus. Returns false (no-op)
        // when no top-nav button is focused, so callers can fall through
        // to other handling.
        //
        // Root-caused bug this fixes: HandleHomeDirectionalNavigation had
        // no HomeButton+Right (or +Up) case, so that key press was left
        // unhandled and WPF's built-in default directional navigation
        // silently took over, moving focus to the next IsTabStop sibling
        // in the nav bar (HOME -> MOVIES -> TV SHOWS) instead of Zino
        // explicitly owning the transition. Every direction is now
        // explicitly claimed and marked handled for every top-nav button,
        // so that fallback can never engage again from this surface.
        //
        // Left/Right chain through _topNavButtons in order. The two
        // existing edge-of-bar designs already present in code are
        // preserved exactly: HOME's left edge wraps down into the Hero's
        // secondary action (My List), and SEARCH's right edge wraps down
        // into the Hero's primary action (Watch Now). Up has no valid
        // target above the top nav bar, so it is consumed safely (kept
        // in place) rather than allowed to escape into WPF's default
        // traversal. Down reuses the existing "go to Watch Now" target
        // for every nav button.

        private bool HandleTopNavDirectional(Key key)
        {
            int currentIndex =
                Array.FindIndex(
                    _topNavButtons,
                    button =>
                        button != null &&
                        button.IsKeyboardFocusWithin);

            if (currentIndex < 0)
            {
                return false;
            }

            switch (key)
            {
                case Key.Down:
                    // Navigation-hierarchy correction: the top nav bar
                    // sits above an intermediate Hero navigation/control
                    // layer (Previous/Next), which itself sits above
                    // Home's content rows - not a direct jump from the
                    // nav bar straight to Watch Now. Down from any
                    // top-nav button now enters that Hero control layer
                    // at its first/default control, HeroPreviousButton -
                    // the same control HeroPreviousButton/HeroNextButton's
                    // own Up case below targets when returning here
                    // (HomeButton.Focus()), confirming this is the
                    // layer's intended entry point.
                    HeroPreviousButton.Focus();
                    return true;

                case Key.Up:
                    // No valid target above the top nav bar - consume the
                    // input safely instead of letting WPF/Playnite decide
                    // where focus goes.
                    return true;

                case Key.Left:
                    if (currentIndex == 0)
                    {
                        HeroMyListButton.Focus();
                    }
                    else
                    {
                        _topNavButtons[currentIndex - 1].Focus();
                    }

                    return true;

                case Key.Right:
                    if (currentIndex == _topNavButtons.Length - 1)
                    {
                        HeroWatchNowButton.Focus();
                    }
                    else
                    {
                        _topNavButtons[currentIndex + 1].Focus();
                    }

                    return true;

                default:
                    return false;
            }
        }


        // =========================================================
        // MEDIA CARD SELECTION
        // =========================================================

        private async void MediaSection_MediaSelected(
            object sender,
            MediaItemSelectedEventArgs e)
        {
            if (e == null || e.Item == null)
            {
                return;
            }

            await HandleMediaSelectedAsync(e.Item);
        }

        // =========================================================
        // HANDLE MEDIA SELECTED (Slice 15: shared by Home, Movies/TV/
        // Anime pages, and Search - was inline in MediaSection_
        // MediaSelected only)
        // =========================================================
        //
        // Every source of a MediaItem selection (Home's MediaSections,
        // CatalogueBrowseView, AnimeBrowseView, SearchView) funnels
        // through this one method, so TV-episode browsing and playback
        // behave identically regardless of where the selection came from -
        // no duplicated selection-handling logic per page.
        private async Task HandleMediaSelectedAsync(MediaItem selectedItem)
        {
            if (selectedItem == null)
            {
                return;
            }

            // Navigation stabilization pass: refuse a second selection
            // while one is still being resolved - see _isResolvingMedia
            // Selection's remarks for why.
            if (_isResolvingMediaSelection)
            {
                return;
            }

            _isResolvingMediaSelection = true;

            try
            {
                // Slice 12: only ContentType.TvShow opens the episode
                // browser. Anime deliberately does NOT go through
                // EpisodeBrowserView - it continues through its existing
                // content/playback path unchanged. Movies are unaffected
                // either way, since GetContentType never returns TvShow
                // for them. An item that already carries Season/Episode
                // is treated as already-resolved and skips browsing,
                // going straight to playback.
                if (GetContentType(selectedItem) == ContentType.TvShow &&
                    !(selectedItem.Season.HasValue && selectedItem.Episode.HasValue))
                {
                    MediaItem episode =
                        await ShowEpisodeBrowserAsync(selectedItem);

                    if (episode == null)
                    {
                        // Browsing cancelled - already torn down and
                        // refocused by ShowEpisodeBrowserAsync itself.
                        return;
                    }

                    selectedItem = episode;
                }

                await PlaySelectedMediaAsync(
                    selectedItem);
            }
            finally
            {
                _isResolvingMediaSelection = false;
            }
        }

        // =========================================================
        // SHOW EPISODE BROWSER
        // =========================================================
        //
        // Creates a fresh EpisodeBrowserView, shows it in
        // EpisodeBrowserHost, and bridges its EpisodeSelected/
        // BrowsingCancelled events into an awaitable Task via
        // TaskCompletionSource - the same bridging pattern already used
        // for ShowSourceSelectorAsync. Returns the episode-shaped
        // MediaItem, or null if the user cancelled.
        //
        // Navigation stabilization pass: this used to tear the host down
        // and call FocusFirstAvailableSection() unconditionally, on both
        // outcomes. That meant Home became visible and interactive again
        // the instant an episode was picked, for the whole duration of
        // the network-bound source resolution that follows in
        // PlaySelectedMediaAsync - reported as "Episode Browser closes ->
        // Home appears -> delayed Source Selector opens". Now, on a
        // cancellation this still tears down and refocuses immediately
        // (the user is genuinely returning to Home). On a real selection,
        // the browser overlay is deliberately left mounted and visible -
        // still the active controller-input target via
        // _activeEpisodeBrowser - so Home is never exposed in between.
        // The caller (HandleMediaSelectedAsync/PlaySelectedMediaAsync) is
        // responsible for tearing it down via
        // TeardownEpisodeBrowserOverlay once it knows what comes next
        // (the Source Selector, or an error/empty-result message).

        private async Task<MediaItem> ShowEpisodeBrowserAsync(
            MediaItem show)
        {
            var browser = new EpisodeBrowserView();

            var completionSource =
                new TaskCompletionSource<MediaItem>();

            EventHandler<EpisodeSelectedEventArgs> onSelected = null;
            EventHandler onCancelled = null;

            onSelected = (sender, args) =>
            {
                completionSource.TrySetResult(args.Episode);
            };

            onCancelled = (sender, args) =>
            {
                completionSource.TrySetResult(null);
            };

            browser.EpisodeSelected += onSelected;
            browser.BrowsingCancelled += onCancelled;

            _activeEpisodeBrowser = browser;

            EpisodeBrowserHost.Children.Add(browser);
            EpisodeBrowserHost.Visibility = Visibility.Visible;

            // Slice 13 (Cinemeta-First Provider & Addon Architecture):
            // Cinemeta needs no credential and is always constructed;
            // TmdbService is only constructed when a TMDB key is actually
            // configured - TMDB is optional, matching the same pattern
            // already used for Home's Hero/catalogue loading above.
            var cinemetaService =
                new CinemetaService();

            TmdbService tmdbService =
                !string.IsNullOrWhiteSpace(_tmdbAccessToken)
                    ? new TmdbService(_tmdbAccessToken)
                    : null;

            await browser.LoadAsync(show, cinemetaService, tmdbService);

            MediaItem result =
                await completionSource.Task;

            browser.EpisodeSelected -= onSelected;
            browser.BrowsingCancelled -= onCancelled;

            if (result == null)
            {
                // Cancelled - the user is genuinely returning to Home,
                // so tear down and refocus right away.
                TeardownEpisodeBrowserOverlay(browser);
            }

            // else: an episode was picked - leave the overlay mounted;
            // see this method's remarks above for why.

            return result;
        }

        // =========================================================
        // TEAR DOWN EPISODE BROWSER OVERLAY (navigation stabilization
        // pass)
        // =========================================================
        //
        // Removes the given EpisodeBrowserView from EpisodeBrowserHost,
        // collapses the host, clears _activeEpisodeBrowser if it still
        // points at this instance, and refocuses Home. No-op if browser
        // is null, so callers that never opened an episode browser for
        // the current selection (movies, anime) can call this
        // unconditionally without an extra branch.

        private void TeardownEpisodeBrowserOverlay(
            EpisodeBrowserView browser)
        {
            if (browser == null)
            {
                return;
            }

            EpisodeBrowserHost.Children.Remove(browser);
            EpisodeBrowserHost.Visibility = Visibility.Collapsed;

            if (ReferenceEquals(_activeEpisodeBrowser, browser))
            {
                _activeEpisodeBrowser = null;
            }

            FocusFirstAvailableSection();
        }

        // =========================================================
        // LOCAL MEDIA (Cinemeta-First Addon Settings Architecture
        // slice addendum - navigation foundation only)
        // =========================================================
        //
        // Local Files is now a core/default addon (CoreAddonSeedingService),
        // so this entry point exists on every fresh install regardless of
        // TMDB/Torrentio/Comet configuration - it depends on none of them.
        //
        // This is deliberately a FOUNDATION, not a full local-media
        // browsing feature: no Local Files catalogue/scan-listing
        // functionality exists anywhere in this codebase today (only
        // stream resolution via StremioAddonRegistry, if something is
        // listening on the local addon's loopback port - see
        // CoreAddonSeedingService's Local Files definition). Building a
        // real browsing UI would mean either fabricating catalogue data
        // (explicitly forbidden) or inventing a new local-library scanning
        // system, which is exactly the "do not build a completely new
        // local-media browsing system" scope limit this task set. So
        // LocalMediaView only ever shows an honest, non-fabricated status
        // view - it reuses this same overlay-host/HandleControllerInput
        // architecture, and the AddonInstance/registry plumbing it will
        // need already exists, so the remaining work is browsing/catalogue
        // logic, left for a dedicated future Local Media slice.
        private async Task ShowLocalMediaAsync()
        {
            var localMediaView = new LocalMediaView();

            bool localFilesConfigured =
                _addonInstances != null &&
                _addonInstances.Exists(instance =>
                    instance != null &&
                    instance.Enabled &&
                    string.Equals(instance.ProviderKey, "localfiles", StringComparison.OrdinalIgnoreCase));

            localMediaView.Initialize(
                localFilesConfigured,
                _libraryLocations?.Count ?? 0);

            var completionSource =
                new TaskCompletionSource<bool>();

            EventHandler onClosed = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            localMediaView.Closed += onClosed;

            _activeLocalMediaView = localMediaView;

            LocalMediaHost.Children.Add(localMediaView);
            LocalMediaHost.Visibility = Visibility.Visible;

            localMediaView.Focus();

            await completionSource.Task;

            localMediaView.Closed -= onClosed;

            LocalMediaHost.Children.Remove(localMediaView);
            LocalMediaHost.Visibility = Visibility.Collapsed;

            _activeLocalMediaView = null;

            FocusFirstAvailableSection();
        }

        private async void LocalMediaButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowLocalMediaAsync();
        }

        // =========================================================
        // PLAY SELECTED MEDIA
        // =========================================================

        private async Task PlaySelectedMediaAsync(
            MediaItem mediaItem)
        {
            if (mediaItem == null)
            {
                return;
            }

            // Navigation stabilization pass: captured once, up front -
            // this is the episode browser overlay ShowEpisodeBrowserAsync
            // deliberately left mounted (null for movies/anime, which
            // never open one). Every exit path below tears it down via
            // TeardownEpisodeBrowserOverlay (a no-op when null) exactly
            // once we know what comes next, instead of it having already
            // disappeared before this method even started.
            EpisodeBrowserView pendingEpisodeBrowser =
                _activeEpisodeBrowser;

            if (string.IsNullOrWhiteSpace(
                    mediaItem.StremioId))
            {
                TeardownEpisodeBrowserOverlay(
                    pendingEpisodeBrowser);

                MessageBox.Show(
                    "This title does not have a Stremio ID yet.");

                return;
            }

            // Defensive safety net: a stale player from a previous session
            // should never still be around by the time a new play attempt
            // starts (the home grid isn't reachable while PlaybackTestHost
            // covers the screen), but this guarantees it rather than
            // relying on that reachability assumption alone.
            if (_stremioPlayer != null)
            {
                ClosePlayer();
            }

            try
            {
                var addonRegistry =
                    StremioAddonRegistry.FromAddonInstances(
                        _addonInstances);

                // Defensive fallback, not the primary path: settings
                // migrates every legacy StremioAddonUrls entry into an
                // AddonInstance as soon as the Settings view model loads
                // (see StremioVideoCenterSettings.SyncLegacyAddonUrls),
                // so in normal operation this loop only re-registers
                // URLs already covered above - AddAddon dedupes by URL,
                // so that is a no-op. It only matters if AddonInstances
                // is unexpectedly null/empty, in which case legacy
                // addon discovery still works exactly as before.
                if (!string.IsNullOrWhiteSpace(_stremioAddonUrls))
                {
                    foreach (string url in _stremioAddonUrls.Split(
                        new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        addonRegistry.AddAddon(url);
                    }
                }

                var discovery =
                    new StremioContentDiscoveryService(
                        addonRegistry);

                RealDebridService realDebrid =
                    string.IsNullOrWhiteSpace(
                        _realDebridApiToken)
                        ? null
                        : new RealDebridService(
                              _realDebridApiToken);

                var resolver =
                    new StreamResolverService(
                        realDebrid);

                var aggregator =
                    new PlaybackSourceAggregationService(
                        discovery,
                        resolver);

                var normalizer =
                    new PlaybackSourceNormalizationService();

                var request = new ContentRequest
                {
                    Type = GetContentType(mediaItem),
                    Title = mediaItem.Title
                };

                // Slice 11: an episode-shaped MediaItem (Season and Episode
                // both populated) is combined into "<imdbId>:<season>:
                // <episode>" via ContentRequest.BuildStremioId() itself -
                // the same mechanism ForTvEpisode already uses - rather
                // than requiring a pre-combined id string on MediaItem.
                if (mediaItem.Season.HasValue &&
                    mediaItem.Episode.HasValue)
                {
                    request.ImdbId = mediaItem.StremioId;
                    request.Season = mediaItem.Season;
                    request.Episode = mediaItem.Episode;
                }
                else
                {
                    // Movie or show-level MediaItem - completely unchanged
                    // from before this slice.
                    request.ExplicitStremioId = mediaItem.StremioId;
                }

                PlaybackSourceSet set =
                    await aggregator.AggregateAsync(
                        request);

                if (set.Sources.Count == 0 &&
                    set.Unresolved.Count == 0)
                {
                    TeardownEpisodeBrowserOverlay(
                        pendingEpisodeBrowser);

                    MessageBox.Show(
                        "Stremio returned no playable streams for:\n\n" +
                        mediaItem.Title);

                    return;
                }

                List<NormalizedPlaybackSource> normalizedSources =
                    normalizer.NormalizeAll(
                        set.Sources);

                // The Source Selector is now actually ready to show -
                // this is the deliberate hand-off point: the episode
                // browser overlay (if any) is replaced by the Source
                // Selector here, rather than having disappeared back when
                // the episode was first picked.
                TeardownEpisodeBrowserOverlay(
                    pendingEpisodeBrowser);

                NormalizedPlaybackSource selected =
                    await ShowSourceSelectorAsync(
                        normalizedSources,
                        mediaItem);

                if (selected == null)
                {
                    return;
                }

                if (_stremioPlayer == null)
                {
                    _stremioPlayer =
                        new MpvPlayer();

                    _stremioPlayer.CloseRequested +=
                        StremioPlayer_CloseRequested;

                    PlaybackTestHost.Children.Add(
                        _stremioPlayer);

                    PlaybackTestHost.Visibility =
                        Visibility.Visible;
                }

                await _stremioPlayer.PlayVideoAsync(
                    selected.Source.Url);
            }
            catch (Exception ex)
            {
                TeardownEpisodeBrowserOverlay(
                    pendingEpisodeBrowser);

                ClosePlayer();

                MessageBox.Show(
                    "Unable to start playback.\n\n" +
                    "Title: " +
                    mediaItem.Title +
                    "\n\n" +
                    ex.Message);
            }
        }

        // =========================================================
        // "SEE MORE" HANDLERS (Slice 15: real navigation)
        // =========================================================

        private async void MoreMovies_Clicked(object sender, EventArgs e)
        {
            await ShowMoviesPageAsync();
        }

        private async void MoreTvShows_Clicked(object sender, EventArgs e)
        {
            await ShowTvPageAsync();
        }

        private async void MoreAnime_Clicked(object sender, EventArgs e)
        {
            await ShowAnimePageAsync();
        }

        // =========================================================
        // TOP NAVIGATION BUTTON HANDLERS (Slice 15)
        // =========================================================

        private async void MoviesButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowMoviesPageAsync();
        }

        private async void TvShowsButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowTvPageAsync();
        }

        private async void AnimeButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowAnimePageAsync();
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowSearchPageAsync();
        }

        // =========================================================
        // MOVIES / TV PAGES (Slice 15)
        // =========================================================
        //
        // Same overlay-host Task/TaskCompletionSource/Closed-event
        // pattern as ShowLocalMediaAsync/ShowEpisodeBrowserAsync/
        // ShowSourceSelectorAsync above. CatalogueBrowseView is shared
        // between Movies and TV (parameterized by isTv) - see its own
        // remarks for why a single reusable control was used instead of
        // two near-identical ones.

        private async Task ShowMoviesPageAsync()
        {
            await ShowCatalogueBrowsePageAsync(isTv: false);
        }

        private async Task ShowTvPageAsync()
        {
            await ShowCatalogueBrowsePageAsync(isTv: true);
        }

        private async Task ShowCatalogueBrowsePageAsync(bool isTv)
        {
            var page = new CatalogueBrowseView();

            var completionSource = new TaskCompletionSource<bool>();

            EventHandler onClosed = null;
            EventHandler<MediaItemSelectedEventArgs> onMediaSelected = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            onMediaSelected = async (sender, args) =>
            {
                if (args?.Item != null)
                {
                    await HandleMediaSelectedAsync(args.Item);
                }
            };

            page.Closed += onClosed;
            page.MediaSelected += onMediaSelected;

            _activeMoviesPage = isTv ? null : page;
            _activeTvPage = isTv ? page : null;

            System.Windows.Controls.Grid host = isTv ? TvPageHost : MoviesPageHost;

            host.Children.Add(page);
            host.Visibility = Visibility.Visible;

            TmdbService tmdbService =
                !string.IsNullOrWhiteSpace(_tmdbAccessToken)
                    ? new TmdbService(_tmdbAccessToken)
                    : null;

            var cinemetaService = new CinemetaService();
            var catalogueService = new CatalogueService(tmdbService, cinemetaService);

            page.Initialize(isTv, catalogueService);

            await completionSource.Task;

            page.Closed -= onClosed;
            page.MediaSelected -= onMediaSelected;

            host.Children.Remove(page);
            host.Visibility = Visibility.Collapsed;

            _activeMoviesPage = null;
            _activeTvPage = null;

            FocusFirstAvailableSection();
        }

        // =========================================================
        // ANIME PAGE (Slice 15)
        // =========================================================

        private async Task ShowAnimePageAsync()
        {
            var page = new AnimeBrowseView();

            var completionSource = new TaskCompletionSource<bool>();

            EventHandler onClosed = null;
            EventHandler<MediaItemSelectedEventArgs> onMediaSelected = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            onMediaSelected = async (sender, args) =>
            {
                if (args?.Item != null)
                {
                    await HandleMediaSelectedAsync(args.Item);
                }
            };

            page.Closed += onClosed;
            page.MediaSelected += onMediaSelected;

            _activeAnimePage = page;

            AnimePageHost.Children.Add(page);
            AnimePageHost.Visibility = Visibility.Visible;

            TmdbService tmdbService =
                !string.IsNullOrWhiteSpace(_tmdbAccessToken)
                    ? new TmdbService(_tmdbAccessToken)
                    : null;

            var cinemetaService = new CinemetaService();
            var catalogueService = new CatalogueService(tmdbService, cinemetaService);
            var animeFlvCatalogueService = new AnimeFlvCatalogueService();

            page.Initialize(catalogueService, animeFlvCatalogueService);

            await completionSource.Task;

            page.Closed -= onClosed;
            page.MediaSelected -= onMediaSelected;

            AnimePageHost.Children.Remove(page);
            AnimePageHost.Visibility = Visibility.Collapsed;

            _activeAnimePage = null;

            FocusFirstAvailableSection();
        }

        // =========================================================
        // SEARCH PAGE (Slice 15)
        // =========================================================

        private async Task ShowSearchPageAsync()
        {
            var page = new SearchView();

            var completionSource = new TaskCompletionSource<bool>();

            EventHandler onClosed = null;
            EventHandler<MediaItemSelectedEventArgs> onMediaSelected = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            onMediaSelected = async (sender, args) =>
            {
                if (args?.Item != null)
                {
                    await HandleMediaSelectedAsync(args.Item);
                }
            };

            page.Closed += onClosed;
            page.MediaSelected += onMediaSelected;

            _activeSearchPage = page;

            SearchPageHost.Children.Add(page);
            SearchPageHost.Visibility = Visibility.Visible;

            var cinemetaService = new CinemetaService();
            var animeFlvCatalogueService = new AnimeFlvCatalogueService();

            page.Initialize(cinemetaService, animeFlvCatalogueService);
            page.FocusSearchBox();

            await completionSource.Task;

            page.Closed -= onClosed;
            page.MediaSelected -= onMediaSelected;

            SearchPageHost.Children.Remove(page);
            SearchPageHost.Visibility = Visibility.Collapsed;

            _activeSearchPage = null;

            FocusFirstAvailableSection();
        }

        // =========================================================
        // GET CONTENT TYPE
        // =========================================================

        private ContentType GetContentType(
            MediaItem mediaItem)
        {
            if (mediaItem == null)
            {
                return ContentType.Movie;
            }

            if (string.Equals(
                    mediaItem.Type,
                    "Anime",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.Anime;
            }

            if (string.Equals(
                    mediaItem.Type,
                    "TV",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            if (string.Equals(
                    mediaItem.Type,
                    "TV Show",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            if (string.Equals(
                    mediaItem.Type,
                    "Series",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContentType.TvShow;
            }

            return ContentType.Movie;
        }

        // =========================================================
        // SHOW SOURCE SELECTOR
        // =========================================================

        private async Task<NormalizedPlaybackSource> ShowSourceSelectorAsync(
            List<NormalizedPlaybackSource> sources,
            MediaItem mediaItem)
        {
            var selector = new SourceSelectorView();

            var completionSource =
                new TaskCompletionSource<NormalizedPlaybackSource>();

            EventHandler<SourceSelectedEventArgs> onSelected = null;
            EventHandler onCancelled = null;

            onSelected = (sender, args) =>
            {
                completionSource.TrySetResult(args.Source);
            };

            onCancelled = (sender, args) =>
            {
                completionSource.TrySetResult(null);
            };

            selector.SourceSelected += onSelected;
            selector.SelectionCancelled += onCancelled;

            _activeSourceSelector = selector;

            SourceSelectorHost.Children.Add(selector);
            SourceSelectorHost.Visibility = Visibility.Visible;

            selector.Populate(sources, mediaItem);

            NormalizedPlaybackSource result =
                await completionSource.Task;

            selector.SourceSelected -= onSelected;
            selector.SelectionCancelled -= onCancelled;

            SourceSelectorHost.Children.Remove(selector);
            SourceSelectorHost.Visibility = Visibility.Collapsed;

            _activeSourceSelector = null;

            FocusFirstAvailableSection();

            return result;
        }
        // =========================================================
        // PLAYER CLOSE
        // =========================================================

        private void StremioPlayer_CloseRequested(
            object sender,
            EventArgs e)
        {
            ClosePlayer();
        }

        private void ClosePlayer()
        {
            if (_stremioPlayer != null)
            {
                try
                {
                    _stremioPlayer.CloseRequested -=
                        StremioPlayer_CloseRequested;
                    _stremioPlayer.DisposePlayer();
                }
                catch
                {
                    // Player cleanup is best-effort.
                }

                try
                {
                    PlaybackTestHost.Children.Remove(
                        _stremioPlayer);
                }
                catch
                {
                    // Ignore removal errors.
                }

                _stremioPlayer = null;
            }

            PlaybackTestHost.Visibility =
                Visibility.Collapsed;

            FocusFirstAvailableSection();
        }

        // =========================================================
        // STREMIO TYPE
        // =========================================================

        private string GetStremioType(
            MediaItem mediaItem)
        {
            if (mediaItem == null)
            {
                return "movie";
            }

            if (string.Equals(
                    mediaItem.Type,
                    "TV",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "series";
            }

            if (string.Equals(
                    mediaItem.Type,
                    "TV Show",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "series";
            }

            if (string.Equals(
                    mediaItem.Type,
                    "Series",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "series";
            }

            return "movie";
        }
    }
}
