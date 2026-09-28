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

        // TEMPORARY DIAGNOSTIC (controller double-input investigation,
        // phase 2 - downstream trace) - logs every PreviewKeyDown this
        // view receives, whatever the source. The point of this one is to
        // catch a case the phase-1 diagnostic could not see: if a single
        // physical controller press produces a synthesized keyboard
        // KeyDown in addition to the direct OnControllerButtonStateChanged
        // callback already proven 1:1, that synthesized key would arrive
        // here and independently re-enter HandleHomeDirectionalNavigation
        // - a second logical move from what looks like one physical
        // press, without either of the phase-1 counters ever seeing two
        // events. Remove once the root cause is confirmed.
        private static int _diagKeyCallSeq;
        private static int _diagNavCallSeq;

        private void StremioVideoCenterView_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            int diagKeySeq =
                Interlocked.Increment(ref _diagKeyCallSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] Key#{0} key={1} alreadyHandled={2}",
                    diagKeySeq,
                    e.Key,
                    e.Handled));

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

            // Controller navigation stabilisation fix (root cause
            // confirmed via the CTRL-DIAG Nav#/Key#/MoveH#/MoveV# trace,
            // then conclusively narrowed by the WH_KEYBOARD_LL diagnostic
            // pass): a physical controller D-pad press is already
            // handled directly by HandleControllerInput. Playnite's own
            // Fullscreen shell separately raises a synthetic arrow-key
            // PreviewKeyDown for generic keyboard-based focus navigation
            // a few milliseconds later, carrying the identical direction
            // - but the diagnostic hook proved this synthetic event never
            // becomes an OS-level keyboard message at all (zero
            // WM_KEYDOWN/WM_SYSKEYDOWN observed for it, injected or not,
            // while genuine physical typing was captured correctly), so
            // it is generated entirely inside Playnite/WPF's managed
            // input pipeline. An OS-level hook can never see it, which is
            // why the earlier LLKHF_INJECTED-based InjectedKeyDetector
            // approach had no effect and has been removed.
            //
            // The fix instead lives entirely at this WPF boundary:
            // HandleControllerInput records, in _pendingControllerEcho
            // Key/_pendingControllerEchoAt, the exact direction it just
            // handled - regardless of whether that direction was routed
            // to Home navigation or to an overlay (Episode Browser,
            // Source Selector, etc.), since the synthetic echo still
            // arrives here either way and must not be allowed to fall
            // through into Home's (possibly hidden) navigation. This is
            // an input-path coordination marker, not a timing debounce:
            // it is direction-specific (a Left echo can never consume a
            // Right press) and is consumed at most once, by the first
            // PreviewKeyDown that matches it. IsControllerEchoEvent below
            // also enforces a short staleness bound purely as a safety
            // net against a marker that never gets consumed (e.g. the
            // echo never arriving) - not as the detection mechanism
            // itself, exactly as the removed hook's own staleness bound
            // was documented. A genuine keyboard arrow pressed by the
            // user is never preceded by a matching HandleControllerInput
            // call, so it always falls through to HandleHomeDirectional
            // Navigation exactly as before.
            if (IsControllerEchoEvent(e.Key))
            {
                e.Handled = true;

                return;
            }

            if (HandleHomeDirectionalNavigation(e.Key))
            {
                e.Handled = true;
            }
        }

        // =========================================================
        // CONTROLLER-ECHO COORDINATION (D-pad double-navigation fix)
        // =========================================================
        //
        // See the remarks above StremioVideoCenterView_PreviewKeyDown's
        // call to IsControllerEchoEvent for the full rationale. This
        // pairs with the marker set in HandleControllerInput below.

        // A short, generous staleness bound - not a debounce window.
        // The diagnostic trace showed the real gap between a controller-
        // handled direction and its synthesized PreviewKeyDown echo is
        // consistently 8-13ms; this only guards against the marker
        // outliving an echo that, for whatever reason, never arrives, so
        // it can never suppress a later, unrelated real keypress.
        private static readonly TimeSpan ControllerEchoStaleAfter =
            TimeSpan.FromMilliseconds(500);

        private Key? _pendingControllerEchoKey;
        private DateTime _pendingControllerEchoAt;

        // Converts a D-pad ControllerInput into the matching directional
        // Key, or null for anything else (A/B/etc.). Shared by
        // HandleControllerInput (to record the pending echo) - there is
        // no other caller, so this never needs to agree with any other
        // button-to-key mapping elsewhere in the codebase.
        private static Key? DirectionalKeyFromControllerInput(
            ControllerInput button)
        {
            switch (button)
            {
                case ControllerInput.DPadLeft:
                    return Key.Left;

                case ControllerInput.DPadRight:
                    return Key.Right;

                case ControllerInput.DPadUp:
                    return Key.Up;

                case ControllerInput.DPadDown:
                    return Key.Down;

                default:
                    return null;
            }
        }

        // Returns true, and consumes the marker, exactly when this key
        // matches the direction HandleControllerInput most recently
        // handled and the marker has not gone stale. Direction-specific
        // and one-shot by construction: a Left echo cannot consume a
        // Right press, and consuming it clears it immediately so it can
        // never also consume a later, unrelated press of the same
        // direction.
        private bool IsControllerEchoEvent(Key key)
        {
            if (_pendingControllerEchoKey.HasValue &&
                _pendingControllerEchoKey.Value == key &&
                (DateTime.UtcNow - _pendingControllerEchoAt) <=
                    ControllerEchoStaleAfter)
            {
                _pendingControllerEchoKey = null;

                return true;
            }

            return false;
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
        // TEMPORARY DIAGNOSTIC (controller double-input investigation,
        // phase 2) - describes whatever currently has keyboard focus, for
        // the Nav#/MoveH#/MoveV# before/after log lines. Remove once the
        // root cause is confirmed.
        private string DescribeCurrentFocusForDiagnostics()
        {
            MediaSection section = GetFocusedSection();

            if (section != null)
            {
                return string.Format(
                    "MediaCard[section={0},index={1}]",
                    section.Title,
                    section.GetFocusedIndex());
            }

            FrameworkElement element =
                Keyboard.FocusedElement as FrameworkElement;

            if (element != null)
            {
                return string.Format(
                    "{0}[{1}]",
                    element.GetType().Name,
                    element.Name);
            }

            return "none";
        }

        private static int _diagMoveVCallSeq;

        private void MoveVertical(
            MediaSection currentSection,
            Key key)
        {
            int diagMoveVSeq =
                Interlocked.Increment(ref _diagMoveVCallSeq);

            List<MediaSection> populatedSections =
                GetPopulatedSections();

            int currentIndex =
                populatedSections.IndexOf(
                    currentSection);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] MoveV#{0} key={1} fromSection={2} fromIndex={3}",
                    diagMoveVSeq,
                    key,
                    currentSection != null ? currentSection.Title : "null",
                    currentIndex));

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

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] MoveV#{0} result toSection={1} toIndex={2} column={3}",
                    diagMoveVSeq,
                    targetSection.Title,
                    targetIndex,
                    columnIndex));

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

        // TEMPORARY DIAGNOSTIC (controller double-input investigation) -
        // logs every call this method receives from
        // StremioVideoCenter.OnControllerButtonStateChanged, and which
        // overlay (if any) is currently active, to confirm whether one
        // Playnite callback ever results in this method itself being
        // entered more than once for the same button. Remove once the
        // root cause is confirmed.
        private static int _diagViewCallSeq;

        public bool HandleControllerInput(ControllerInput button)
        {
            int seq = Interlocked.Increment(ref _diagViewCallSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] View#{0} button={1} activeEpisodeBrowser={2} activeSourceSelector={3} activeLocalMedia={4} activeMovies={5} activeTv={6} activeAnime={7} activeSearch={8} activePlayer={9}",
                    seq,
                    button,
                    _activeEpisodeBrowser != null,
                    _activeSourceSelector != null,
                    _activeLocalMediaView != null,
                    _activeMoviesPage != null,
                    _activeTvPage != null,
                    _activeAnimePage != null,
                    _activeSearchPage != null,
                    _stremioPlayer != null));

            // Controller navigation stabilisation fix: record the exact
            // D-pad direction this call is about to handle, before any
            // overlay routing below. Playnite's Fullscreen shell raises a
            // synthetic arrow-key PreviewKeyDown for the same physical
            // press regardless of which overlay (if any) currently owns
            // controller input, so the marker must be set here - at the
            // single point every D-pad press passes through - rather
            // than only inside the Home-navigation switch further down.
            // See IsControllerEchoEvent's remarks (above StremioVideoCenter
            // View_PreviewKeyDown) for how this is consumed.
            Key? directionalKey = DirectionalKeyFromControllerInput(button);

            if (directionalKey.HasValue)
            {
                _pendingControllerEchoKey = directionalKey;
                _pendingControllerEchoAt = DateTime.UtcNow;
            }

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
        // TEMPORARY DIAGNOSTIC (Part 2, controller-A-delay investigation)
        // - sequence counter for the SelCard# log lines below. Purely
        // observational - no behaviour here is changed.
        private static int _diagSelCardSeq;

        private bool SelectFocusedMediaCard()
        {
            int diagSeq = Interlocked.Increment(ref _diagSelCardSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} begin focusedElement={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    Keyboard.FocusedElement != null
                        ? Keyboard.FocusedElement.GetType().Name
                        : "(null)"));

            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(
                    new RoutedEventArgs(
                        ButtonBase.ClickEvent));

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} end path=Button (Click raised synchronously)",
                        diagSeq,
                        DateTime.UtcNow));

                return true;
            }

            MediaSection currentSection = GetFocusedSection();

            if (currentSection == null)
            {
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} end path=NoFocusedSection result=false",
                        diagSeq,
                        DateTime.UtcNow));

                return false;
            }

            MediaCard focusedCard =
                FindFocusedMediaCard(currentSection);

            if (focusedCard == null)
            {
                currentSection.FocusFirstCard();

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} end path=NoFocusedCard->FocusFirstCard",
                        diagSeq,
                        DateTime.UtcNow));

                return true;
            }

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} raising MediaCard.SelectedEvent (synchronous - MediaSection_MediaSelected fires from inside this call)",
                    diagSeq,
                    DateTime.UtcNow));

            focusedCard.RaiseEvent(
                new RoutedEventArgs(
                    MediaCard.SelectedEvent));

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SelCard#{0} time={1:HH:mm:ss.fff} end path=MediaCard result=true (note: MediaSelected handler is async void - this end may log before that handler's own work finishes)",
                    diagSeq,
                    DateTime.UtcNow));

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
            // TEMPORARY DIAGNOSTIC (controller double-input investigation,
            // phase 2) - this is the single shared method both the
            // controller path (HandleControllerInput) and the keyboard
            // path (StremioVideoCenterView_PreviewKeyDown) call. Logging
            // every entry here, correlated by timestamp against the
            // View#/Key# sequence numbers already logged by its two
            // callers, shows whether a single physical press results in
            // this method running once or twice. Remove once the root
            // cause is confirmed.
            int diagNavSeq =
                Interlocked.Increment(ref _diagNavCallSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] Nav#{0} HandleHomeDirectionalNavigation key={1} focusBefore={2}",
                    diagNavSeq,
                    key,
                    DescribeCurrentFocusForDiagnostics()));

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
                    // Home vertical nav fix: the Hero control layer sits
                    // directly above the Hero's own action controls
                    // (Watch Now/My List), which in turn sit above
                    // Home's content rows. Down from here must enter
                    // that action-control layer at its first/default
                    // control, Watch Now - not skip past it straight to
                    // the first content row. This mirrors HeroWatchNow
                    // Button/HeroMyListButton's own Up case below
                    // targeting HeroPreviousButton, confirming this is
                    // the intended two-way link between the two layers.
                    HeroWatchNowButton.Focus();

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
                    // Home vertical nav fix: My List's Up goes back to
                    // Watch Now (mirroring Watch Now's own Down going to
                    // My List, immediately below); only Watch Now's Up
                    // continues on up to the Hero control layer above
                    // it, exactly as before this fix.
                    if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        HeroWatchNowButton.Focus();
                    }
                    else
                    {
                        HeroPreviousButton.Focus();
                    }

                    return true;
                }

                if (key == Key.Down)
                {
                    // Home vertical nav fix: Watch Now and My List are
                    // two separate stops in the vertical chain (Hero ->
                    // Watch Now -> My List -> first content row), not a
                    // single combined layer that both drop straight
                    // through to content. Watch Now's Down now goes to
                    // My List; only My List's Down continues on to the
                    // first populated content row.
                    if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        HeroMyListButton.Focus();

                        return true;
                    }

                    // Navigation stabilization pass (preserved): was
                    // sections[0] directly - if MyList/ContinueWatching
                    // (or any earlier row) is present but empty, index 0
                    // is not actually focusable and this silently did
                    // nothing. First POPULATED section is the correct
                    // target per the intended Home design.
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

        // TEMPORARY DIAGNOSTIC (Part 2, controller-A-delay investigation)
        // - sequence counters for the MediaSel#/HandleSel# log lines
        // below. Purely observational - no behaviour here is changed.
        private static int _diagMediaSelSeq;
        private static int _diagHandleSelSeq;

        private async void MediaSection_MediaSelected(
            object sender,
            MediaItemSelectedEventArgs e)
        {
            int diagSeq = Interlocked.Increment(ref _diagMediaSelSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] MediaSel#{0} time={1:HH:mm:ss.fff} fired item={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    (e != null && e.Item != null) ? e.Item.Title : "(null)"));

            if (e == null || e.Item == null)
            {
                return;
            }

            await HandleMediaSelectedAsync(e.Item);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] MediaSel#{0} time={1:HH:mm:ss.fff} HandleMediaSelectedAsync returned",
                    diagSeq,
                    DateTime.UtcNow));
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
            int diagSeq = Interlocked.Increment(ref _diagHandleSelSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} begin item={2} isResolvingMediaSelection={3}",
                    diagSeq,
                    DateTime.UtcNow,
                    selectedItem != null ? selectedItem.Title : "(null)",
                    _isResolvingMediaSelection));

            if (selectedItem == null)
            {
                return;
            }

            // Navigation stabilization pass: refuse a second selection
            // while one is still being resolved - see _isResolvingMedia
            // Selection's remarks for why.
            if (_isResolvingMediaSelection)
            {
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} end reason=AlreadyResolving (re-entrant call ignored)",
                        diagSeq,
                        DateTime.UtcNow));

                return;
            }

            _isResolvingMediaSelection = true;

            try
            {
                // Slice 12: only ContentType.TvShow opened the episode
                // browser originally. Slice 15 (Anime Architecture Audit
                // + Kitsu Migration + Provider Role Correction) extends
                // this to ContentType.Anime too, per this slice's own
                // architectural goal ("Anime Series -> Season -> Episode
                // -> Source Selector -> MPV", the same hierarchy as TV) -
                // previously an Anime-typed MediaItem (from either the
                // TMDB-backed rows or the AnimeFLV/Kitsu browse/search
                // paths) skipped episode browsing entirely and went
                // straight to playback as if it were a single show-level
                // lookup. EpisodeBrowserView.LoadAsync itself already
                // handles all three id shapes this can now reach it with
                // (tt-based via Cinemeta/TMDB, kitsu-based via Kitsu -
                // see its own remarks). Movies are unaffected either
                // way, since GetContentType never returns TvShow or
                // Anime for them. An item that already carries Season/
                // Episode is treated as already-resolved and skips
                // browsing, going straight to playback.
                ContentType selectedContentType = GetContentType(selectedItem);

                if ((selectedContentType == ContentType.TvShow || selectedContentType == ContentType.Anime) &&
                    !(selectedItem.Season.HasValue && selectedItem.Episode.HasValue))
                {
                    StremioVideoCenterLogger.Info(
                        string.Format(
                            "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} awaiting ShowEpisodeBrowserAsync",
                            diagSeq,
                            DateTime.UtcNow));

                    MediaItem episode =
                        await ShowEpisodeBrowserAsync(selectedItem);

                    StremioVideoCenterLogger.Info(
                        string.Format(
                            "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} ShowEpisodeBrowserAsync returned episode={2}",
                            diagSeq,
                            DateTime.UtcNow,
                            episode != null ? episode.Title : "(null - cancelled)"));

                    if (episode == null)
                    {
                        // Browsing cancelled - already torn down and
                        // refocused by ShowEpisodeBrowserAsync itself.
                        return;
                    }

                    selectedItem = episode;
                }

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} awaiting PlaySelectedMediaAsync",
                        diagSeq,
                        DateTime.UtcNow));

                await PlaySelectedMediaAsync(
                    selectedItem);

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} PlaySelectedMediaAsync returned",
                        diagSeq,
                        DateTime.UtcNow));
            }
            finally
            {
                _isResolvingMediaSelection = false;

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] HandleSel#{0} time={1:HH:mm:ss.fff} end",
                        diagSeq,
                        DateTime.UtcNow));
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

            // Slice 15 Kitsu Migration: needed for a Kitsu-sourced show
            // (StremioId starting "kitsu:") - see EpisodeBrowserView.
            // LoadAsync's own routing. Zero-config, always constructed,
            // exactly like cinemetaService above.
            var kitsuService = new KitsuService();

            await browser.LoadAsync(show, cinemetaService, tmdbService, kitsuService);

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

        // TEMPORARY DIAGNOSTIC (Part 2, controller-A-delay investigation)
        // - sequence counter for the Play# log lines below. Purely
        // observational - no behaviour here is changed.
        private static int _diagPlaySeq;

        private async Task PlaySelectedMediaAsync(
            MediaItem mediaItem)
        {
            int diagSeq = Interlocked.Increment(ref _diagPlaySeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} begin item={2} stremioId={3}",
                    diagSeq,
                    DateTime.UtcNow,
                    mediaItem != null ? mediaItem.Title : "(null)",
                    mediaItem != null ? mediaItem.StremioId : "(null)"));

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
                //
                // Slice 15 (Anime Architecture Audit + Kitsu Migration +
                // Provider Role Correction): a Kitsu-sourced episode
                // (mediaItem.StremioId starting "kitsu:") does NOT use
                // this tt/Season/Episode shape at all - the real,
                // documented Kitsu/Stremio episode id is
                // "kitsu:<id>:<episodeNumber>", with NO season component
                // (see KitsuService's own remarks and EpisodeBrowserView.
                // BuildEpisodeMediaItem) - so it is built directly as
                // ExplicitStremioId instead of going through ImdbId/
                // Season/Episode, which BuildStremioId() would otherwise
                // combine into the wrong (tt-style, season-inclusive)
                // shape.
                bool isKitsuId =
                    !string.IsNullOrWhiteSpace(mediaItem.StremioId) &&
                    mediaItem.StremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);

                if (mediaItem.Season.HasValue &&
                    mediaItem.Episode.HasValue &&
                    isKitsuId)
                {
                    request.ExplicitStremioId =
                        mediaItem.StremioId + ":" + mediaItem.Episode.Value;
                }
                else if (mediaItem.Season.HasValue &&
                    mediaItem.Episode.HasValue)
                {
                    request.ImdbId = mediaItem.StremioId;
                    request.Season = mediaItem.Season;
                    request.Episode = mediaItem.Episode;
                }
                else
                {
                    // Movie or show-level MediaItem - completely unchanged
                    // from before this slice (still handles a kitsu-
                    // prefixed show-level id too, e.g. if a future caller
                    // ever sends one with no Season/Episode - used
                    // verbatim, same as any other ExplicitStremioId).
                    request.ExplicitStremioId = mediaItem.StremioId;
                }

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} awaiting aggregator.AggregateAsync (discovery + per-stream resolution)",
                        diagSeq,
                        DateTime.UtcNow));

                PlaybackSourceSet set =
                    await aggregator.AggregateAsync(
                        request);

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} aggregator.AggregateAsync returned sources={2} unresolved={3}",
                        diagSeq,
                        DateTime.UtcNow,
                        set.Sources.Count,
                        set.Unresolved.Count));

                // Slice 15 Workstream B (no-stream UX correction): "no
                // playable streams" is a valid, expected result - not an
                // exceptional failure - so it no longer short-circuits
                // into a blocking MessageBox here. The code below already
                // handles a zero-source case gracefully on its own: it
                // was already reached whenever Sources was empty but
                // Unresolved was not (e.g. every candidate hit the
                // Real-Debrid resolution limit), which NormalizeAll turns
                // into an empty normalizedSources list, which
                // ShowSourceSelectorAsync/SourceSelectorView.Populate
                // already detects and shows its own existing "No
                // playable sources found." empty state for (see
                // SourceSelectorView.ShowEmptyState) - complete with a
                // working Back button wired to the same
                // SelectionCancelled flow as everywhere else. Removing
                // this early-exit branch simply lets the true "nothing
                // at all" case (Sources.Count == 0 && Unresolved.Count ==
                // 0) reach that exact same, already-proven path instead
                // of a separate MessageBox branch - no new UI or
                // plumbing was needed. A genuine unexpected exception
                // (network failure, malformed response, etc.) is
                // unaffected - the catch block below still reports that
                // separately via its own MessageBox, so a real error is
                // never mistaken for a legitimate zero-result response.
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

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} awaiting ShowSourceSelectorAsync with {2} normalized source(s)",
                        diagSeq,
                        DateTime.UtcNow,
                        normalizedSources.Count));

                NormalizedPlaybackSource selected =
                    await ShowSourceSelectorAsync(
                        normalizedSources,
                        mediaItem);

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} ShowSourceSelectorAsync returned selected={2}",
                        diagSeq,
                        DateTime.UtcNow,
                        selected != null ? "true" : "(null - cancelled)"));

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

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} awaiting PlayVideoAsync",
                        diagSeq,
                        DateTime.UtcNow));

                await _stremioPlayer.PlayVideoAsync(
                    selected.Source.Url);

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} end (PlayVideoAsync returned)",
                        diagSeq,
                        DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "[CTRL-DIAG] Play#{0} time={1:HH:mm:ss.fff} end (exception: {2})",
                        diagSeq,
                        DateTime.UtcNow,
                        ex.Message));

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
            var kitsuService = new KitsuService();

            page.Initialize(catalogueService, animeFlvCatalogueService, kitsuService);

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
            var kitsuService = new KitsuService();

            page.Initialize(cinemetaService, kitsuService);
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

        // TEMPORARY DIAGNOSTIC (Part 2, controller-A-delay investigation)
        // - sequence counter for the SrcSel# log lines below. Purely
        // observational - no behaviour here is changed.
        private static int _diagSrcSelSeq;

        private async Task<NormalizedPlaybackSource> ShowSourceSelectorAsync(
            List<NormalizedPlaybackSource> sources,
            MediaItem mediaItem)
        {
            int diagSeq = Interlocked.Increment(ref _diagSrcSelSeq);

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SrcSel#{0} time={1:HH:mm:ss.fff} begin sourceCount={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    sources != null ? sources.Count : 0));

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

            // This is the point the user's diagnostic request calls out
            // specifically: the Source Selector control has now been
            // constructed, added to the visual tree, made Visible, and
            // populated with data - i.e. actually displayed - and
            // control is about to yield on completionSource.Task waiting
            // for the user to pick a source (or cancel).
            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SrcSel#{0} time={1:HH:mm:ss.fff} Source Selector created/populated/visible - now awaiting user selection",
                    diagSeq,
                    DateTime.UtcNow));

            NormalizedPlaybackSource result =
                await completionSource.Task;

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[CTRL-DIAG] SrcSel#{0} time={1:HH:mm:ss.fff} end user chose={2}",
                    diagSeq,
                    DateTime.UtcNow,
                    result != null ? "a source" : "(null - cancelled)"));

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
