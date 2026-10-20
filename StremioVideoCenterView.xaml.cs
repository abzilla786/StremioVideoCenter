using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Events;
using StremioVideoCenterControls;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenter.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
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
        // MOVIE DETAILS (Slice 19B)
        // =========================================================
        //
        // Non-null only while a MovieDetailsView is shown for one movie
        // selection. It stays mounted (beneath the Source Selector and the
        // player) for the whole movie flow, so Back from the Source
        // Selector lands on it. _movieDetailsBusy is true while Watch Now
        // is resolving sources, during which Details ignores Back/Watch Now.

        private MovieDetailsView _activeMovieDetails;
        private bool _movieDetailsBusy;

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
        // PROFILE SELECTION (Slice 16A - Profile Foundation)
        // =========================================================
        //
        // Non-null only while a ProfileSelectionView is actively shown -
        // same lifecycle discipline as every other overlay above: created
        // fresh per visit, torn down immediately once a profile is
        // selected. Checked FIRST in RouteButtonToActiveOverlayOrHome
        // (the most specific/modal overlay of all - it owns startup
        // itself, before Home has ever loaded).

        private ProfileSelectionView _activeProfileSelection;

        // The sole owner of StremioVideoCenterSettings.Profiles/
        // ActiveProfileId - constructed once in StremioVideoCenter.cs
        // (where the live settings object and SavePluginSettings are
        // both available) and handed to this view via configuration. Can
        // be null (see configuration remarks) if settings somehow failed
        // to initialize - every call site below guards against that
        // rather than assuming it is always present.
        private readonly ProfileService _profileService;

        // =========================================================
        // WATCH STATE (Slice 16B - Persistent Profile-Owned Watch State)
        // =========================================================
        //
        // The sole owner of StremioVideoCenterSettings.WatchStates -
        // same construction/ownership pattern as _profileService above
        // (built once in StremioVideoCenter.cs, handed in via
        // configuration). Can be null under the same conditions
        // _profileService can be null; every call site below guards
        // against that.
        private readonly WatchStateService _watchStateService;

        // =========================================================
        // WATCH HISTORY (Slice 16C - Continue Watching + History)
        // =========================================================
        //
        // The sole owner of StremioVideoCenterSettings.WatchHistory -
        // same construction/ownership pattern as _watchStateService
        // above (built once in StremioVideoCenter.cs, handed in via
        // configuration). Can be null under the same conditions
        // _watchStateService can be null; every call site below guards
        // against that.
        private readonly WatchHistoryService _watchHistoryService;

        // =========================================================
        // MY LIST (Slice 17A - My List Core)
        // =========================================================
        //
        // The sole owner of StremioVideoCenterSettings.MyListEntries -
        // same construction/ownership pattern as _watchHistoryService
        // above (built once in StremioVideoCenter.cs, handed in via
        // configuration). Can be null under the same conditions
        // _watchHistoryService can be null. Not yet called anywhere in
        // this view - My List UI (media selection Add/Remove, Hero + My
        // List) is Slice 17B/later; this field only establishes the
        // foundation the service-layer audit called for.
        private readonly MyListService _myListService;

        // Non-null only while a HistoryView is actively shown - same
        // lifecycle discipline as _activeLocalMediaView/_activeSearchPage:
        // created fresh per visit, torn down immediately when the user
        // closes it. Checked in RouteButtonToActiveOverlayOrHome alongside
        // the other dedicated pages.
        private HistoryView _activeHistoryView;

        // Non-null only while a MyListView is actively shown (Slice 17B) -
        // same lifecycle discipline as _activeHistoryView directly above:
        // created fresh per visit, torn down immediately when the user
        // closes it. Checked in RouteButtonToActiveOverlayOrHome alongside
        // the other dedicated pages.
        private MyListView _activeMyListView;

        // Slice 18A: everything about the CURRENT playback attempt
        // (profile captured at start, canonical key, content type, media
        // item, selected source, optional resume position, and the other
        // candidate sources for failure recovery). Set when playback is
        // started, handed down to MpvPlayer/PlayerSession, and cleared by
        // ClosePlayer. Null when no player is open. Replaces the former
        // _activePlayback* and _recovery* fields.
        private PlaybackContext _playbackContext;

        // Created only once mpv confirms the file loaded
        // (StremioPlayer_PlaybackEstablished). Null before that and when
        // Watch State is unavailable, so a failed/aborted attempt records
        // nothing.
        private PlaybackProgressRecorder _progressRecorder;

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
        // PERSISTENT HERO NAVIGATION (quick-nav row on Movies/TV/Anime)
        // =========================================================
        //
        // Set (synchronously, before the page's own Closed event fires -
        // see CatalogueBrowseView.RaiseNavigationRequested/AnimeBrowseView
        // .RaiseNavigationRequested) by a quick-nav Movies/TV Shows/Anime
        // button click on whichever page is currently open. The page
        // tears itself down through the exact same Closed-event path as
        // the Back button; once ShowCatalogueBrowsePageAsync/
        // ShowAnimePageAsync finish their own cleanup (host/Children,
        // Visibility, _active*Page) they check this field and, if set,
        // open the requested destination next instead of just returning
        // to Home - see NavigateToPendingDestinationAsync. Only one page
        // is ever open at a time (same invariant as _activeMoviesPage/
        // _activeTvPage/_activeAnimePage/_activeSearchPage), so a single
        // shared field is enough.
        private string _pendingPageNavigationDestination;

        // Slice 21 Stage 1 (F6): the top-navigation button the current
        // Movies/TV/Anime page chain was opened from, so focus can return
        // to it when the chain ends (see RestoreFocusToNavigationButton).
        // Null when the page was opened from a Home row ("See more"),
        // which keeps the usual return-to-Home focus.
        private Button _catalogueReturnFocusButton;

        // =========================================================
        // LOCAL MEDIA LIBRARY LOCATIONS (Slice 15 - architecture only)
        // =========================================================

        private readonly List<LibraryLocation> _libraryLocations;

        // Local Files: the plugin-owned library (folders, scan, index,
        // matching, local playback sources). Null when unavailable - every
        // use below degrades to "no local sources".
        private readonly LocalLibraryManager _localLibrary;

        // =========================================================
        // STARTUP STATE
        // =========================================================

        private bool _startupSequenceStarted;


        // =========================================================
        // REAL-DEBRID CREDENTIAL
        // =========================================================

        // Slice 20 Stage 1B: read through the live provider when one was
        // supplied (see StremioVideoCenterConfiguration), so the value is
        // never older than the saved settings; otherwise the snapshot.
        private readonly string _realDebridApiTokenSnapshot;
        private readonly Func<string> _realDebridApiTokenProvider;

        private string _realDebridApiToken
        {
            get
            {
                return _realDebridApiTokenProvider != null
                    ? _realDebridApiTokenProvider()
                    : _realDebridApiTokenSnapshot;
            }
        }


        // =========================================================
        // STREMIO ADDON URLS (newline-delimited, legacy)
        // =========================================================

        private readonly string _stremioAddonUrlsSnapshot;
        private readonly Func<string> _stremioAddonUrlsProvider;

        private string _stremioAddonUrls
        {
            get
            {
                return _stremioAddonUrlsProvider != null
                    ? _stremioAddonUrlsProvider()
                    : _stremioAddonUrlsSnapshot;
            }
        }


        // =========================================================
        // STREMIO ADDON INSTANCES (structured)
        // =========================================================

        private readonly List<AddonInstance> _addonInstancesSnapshot;
        private readonly Func<List<AddonInstance>> _addonInstancesProvider;

        // Slice 20 Stage 3: external subtitle search for the playing
        // content. Which addons are asked, and whether one can serve this
        // content id, is decided by the generic addon query from each
        // addon's own manifest; this view only supplies the installed addon
        // list (a snapshot, so a settings change cannot alter a running
        // search). Nothing here runs until the selector is opened.
        private Task<SubtitleDiscoveryResult> DiscoverExternalSubtitlesAsync(
            PlaybackContext context,
            CancellationToken cancellationToken)
        {
            // A local file's own id means nothing to a subtitle addon (and is
            // not sent to one): nothing to search for.
            if (LocalLibraryManager.IsLocalItemKey(context.CanonicalKey))
            {
                return Task.FromResult(new SubtitleDiscoveryResult { ContentUnidentified = true });
            }

            List<AddonInstance> addons = _addonInstances;

            return SubtitleDiscoveryService.DiscoverForContentAsync(
                addons != null ? addons.ToList() : new List<AddonInstance>(),
                context.ContentType,
                context.CanonicalKey,
                cancellationToken);
        }

        private List<AddonInstance> _addonInstances
        {
            get
            {
                return _addonInstancesProvider != null
                    ? _addonInstancesProvider()
                    : _addonInstancesSnapshot;
            }
        }


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

        private void MainScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveHeroSize(e.NewSize.Width, e.NewSize.Height);
        }

        // Scales the hero (design 1240 x 698, see HeroLayout) uniformly from the
        // viewport WPF actually provides. Everything is in DIPs, straight from
        // layout, so DPI, monitor and window changes arrive as SizeChanged and are
        // handled the same way; nothing here knows about any display, device or
        // resolution. The info panel follows the frame through the layout rows.
        private void ApplyResponsiveHeroSize(double viewportWidth, double viewportHeight)
        {
            StremioVideoCenterControls.Player.HeroLayoutResult layout =
                StremioVideoCenterControls.Player.HeroLayout.Compute(viewportWidth, viewportHeight);

            HeroCanvasScale.ScaleX = layout.Scale;
            HeroCanvasScale.ScaleY = layout.Scale;
            HeroContentScale.ScaleX = layout.Scale;
            HeroContentScale.ScaleY = layout.Scale;
        }

        public StremioVideoCenterView(
            StremioVideoCenterConfiguration configuration = null)
        {
            InitializeComponent();

            // Mouse click-and-drag scrolling (Slice 20 Stage 3).
            DragScroll.Attach(MainScrollViewer, DragScrollAxis.Vertical);

            // The Home hero has a design size (1240 x 698); keep it within the
            // space the window actually has (see ApplyResponsiveHeroSize).
            MainScrollViewer.SizeChanged += MainScrollViewer_SizeChanged;

            configuration = configuration ?? new StremioVideoCenterConfiguration();

            _realDebridApiTokenSnapshot =
                configuration.RealDebridApiKey;

            _realDebridApiTokenProvider =
                configuration.RealDebridApiKeyProvider;

            _stremioAddonUrlsSnapshot =
                configuration.StremioAddonUrls;

            _stremioAddonUrlsProvider =
                configuration.StremioAddonUrlsProvider;

            _addonInstancesSnapshot =
                configuration.AddonInstances;

            _addonInstancesProvider =
                configuration.AddonInstancesProvider;

            _libraryLocations =
                configuration.LibraryLocations;

            _localLibrary =
                configuration.LocalLibrary;

            _profileService =
                configuration.ProfileService;

            _watchStateService =
                configuration.WatchStateService;

            _watchHistoryService =
                configuration.WatchHistoryService;

            _myListService =
                configuration.MyListService;

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
                    RecommendedSection,
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
                    SearchButton,
                    SwitchProfileButton
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

            // Slice 16A (Profile Foundation) startup gate: "Application
            // starts -> Load persisted profiles -> Profile Selection
            // screen -> User selects profile -> Set ActiveProfileId ->
            // Load profile-specific personal data -> Home". This runs
            // and completes BEFORE any Hero/catalogue loading below, so
            // Home is never shown, even briefly, before a profile has
            // been chosen. ShowProfileSelectionAsync returns only after
            // the user has selected a profile (see that method's own
            // remarks - there is no cancel path), and _profileService
            // being null (defensive only) simply skips this gate rather
            // than blocking startup.
            await ShowProfileSelectionAsync(isSwitching: false);

            if (!IsLoaded)
            {
                // Lifecycle fix (existing convention, see the identical
                // check further below for the hero rotation loop): the
                // window may already have been closed while profile
                // selection was awaited.
                return;
            }

            if (DataContext is HomeViewModel vm)
            {
                // Hero (Cinemeta Movie/TV + Kitsu Anime) and the Home
                // catalogue rows (Cinemeta Movie/TV, Kitsu Anime) always
                // attempt to load; neither needs any credential.
                Task heroTask =
                    LoadHeroSafeAsync(vm);

                Task catalogueTask =
                    LoadCatalogueSafeAsync(vm);

                await Task.WhenAll(
                    heroTask,
                    catalogueTask);
            }

            UpdateHeroIndicators();

            // Slice 17B: the real Cinemeta/Kitsu-sourced Hero items just
            // finished loading (or the fallback Hero data remains) -
            // refresh the My List button now that CurrentHeroItem reflects
            // whichever item index 0 actually is. OnActiveProfileChanged
            // already ran once above (inside ShowProfileSelectionAsync,
            // before Hero had loaded), so this is specifically to pick up
            // real Hero content instead of the static fallback slide.
            RefreshHeroMyListButtonState();

            // Slice 17C: Hero items are now known, so the recommendations
            // can exclude them.
            RefreshRecommendations(clearImmediately: false);

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
            HomeViewModel vm)
        {
            try
            {
                // Cinemeta (Movie/TV) + Kitsu (Anime) candidate sourcing - see
                // HeroRecommendationService's own class remarks. Fresh,
                // local instances, matching this project's existing
                // convention of constructing these per call site rather
                // than sharing a field-level instance (see
                // LoadCatalogueSafeAsync's identical CinemetaService
                // construction just below, and ShowEpisodeBrowserAsync's
                // own CinemetaService/KitsuService construction).
                CinemetaService cinemetaService =
                    new CinemetaService();

                KitsuService kitsuService =
                    new KitsuService();

                HeroRecommendationService recommendationService =
                    new HeroRecommendationService(
                        cinemetaService,
                        kitsuService);

                await vm.LoadHeroRecommendationsAsync(
                    recommendationService);
            }
            catch (Exception ex)
            {
                // HomeViewModel retains its fallback Hero data. Slice 21: the
                // reason is now recorded (type + path/URL-stripped message).
                StremioVideoCenterLogger.Info(
                    "Home hero recommendations failed to load: " +
                    UserFacingErrors.ForLog(ex));
            }
        }

        // =========================================================
        // LOAD CATALOGUE (fail-safe)
        // =========================================================
        //
        // Real Catalogue & Content Foundation: replaces the hardcoded
        // Top 10 rows and populates the new Trending/Recently Released
        // rows with real content (Cinemeta for Movie/TV, Kitsu for Anime).

        private async Task LoadCatalogueSafeAsync(
            HomeViewModel vm)
        {
            try
            {
                CinemetaService cinemetaService =
                    new CinemetaService();

                KitsuService kitsuService =
                    new KitsuService();

                CatalogueService catalogueService =
                    new CatalogueService(cinemetaService, kitsuService);

                await vm.LoadCatalogueAsync(
                    catalogueService);
            }
            catch (Exception ex)
            {
                // HomeViewModel retains its fallback/test catalogue
                // data for any row that didn't load. Slice 21: the reason
                // is now recorded (type + path/URL-stripped message).
                StremioVideoCenterLogger.Info(
                    "Home catalogue failed to load: " +
                    UserFacingErrors.ForLog(ex));
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
        // SHUTDOWN CLEAN-UP (Slice 22)
        // =========================================================
        //
        // Called by the plugin when the Zino window closes for ANY reason
        // (including Playnite closing and taking its owned window with it)
        // and when Playnite stops. Until now a close while a video was
        // playing never reached ClosePlayer, so the last position was not
        // saved and mpv was left to the process-exit clean-up.
        //
        // Does only what is needed to leave nothing running: stop the
        // rotation loop, cancel the recommendation calculation, persist the
        // last VALID playback position (a no-op when playback never
        // established) and dispose the player this view created - which
        // ends only the mpv process that player's session started. It does
        // not touch the window, focus, navigation or any other process, and
        // it does not refresh UI that is about to go away.
        //
        // Idempotent: the window-closed path and the application-stopped
        // path can both call it.
        private bool _shutdownCleanupDone;

        public void ShutdownForClose()
        {
            if (_shutdownCleanupDone)
            {
                return;
            }

            _shutdownCleanupDone = true;

            try
            {
                StopHeroBackdropRotation();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Shutdown: stopping the hero rotation failed: " + UserFacingErrors.ForLog(ex));
            }

            try
            {
                CancellationTokenSource recommendations = _recommendationCts;
                _recommendationCts = null;

                if (recommendations != null)
                {
                    recommendations.Cancel();
                }
            }
            catch (Exception ex)
            {
                // Already disposed or cancelled: nothing left to stop.
                StremioVideoCenterLogger.Info(
                    "Shutdown: cancelling recommendations: " + ex.GetType().Name);
            }

            // Invalidate any episode transition still resolving streams.
            _playbackGeneration++;

            if (_stremioPlayer == null)
            {
                return;
            }

            try
            {
                FlushPlaybackProgress();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Shutdown: saving the playback position failed: " + UserFacingErrors.ForLog(ex));
            }

            try
            {
                _stremioPlayer.CloseRequested -= StremioPlayer_CloseRequested;
                _stremioPlayer.PlaybackStateChanged -= StremioPlayer_PlaybackStateChanged;
                _stremioPlayer.PlaybackFailed -= StremioPlayer_PlaybackFailed;
                _stremioPlayer.PlaybackEstablished -= StremioPlayer_PlaybackEstablished;
                _stremioPlayer.PreviousEpisodeRequested -= StremioPlayer_PreviousEpisodeRequested;
                _stremioPlayer.NextEpisodeRequested -= StremioPlayer_NextEpisodeRequested;
                _stremioPlayer.EndOfFileReached -= StremioPlayer_EndOfFileReached;
                _stremioPlayer.OverlayKeyPreview -= StremioVideoCenterView_PreviewKeyDown;
                _stremioPlayer.DisposePlayer();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Shutdown: disposing the player failed: " + UserFacingErrors.ForLog(ex));
            }

            _stremioPlayer = null;
            _playbackContext = null;
            _progressRecorder = null;
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

            // Slice 17B: the My List button must immediately reflect
            // whichever Hero item the carousel just moved to - never the
            // previous item's stale membership state.
            RefreshHeroMyListButtonState();

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

            // Slice 17B: same reasoning as ChangeHero above - the My List
            // button must track whichever Hero item is now displayed.
            RefreshHeroMyListButtonState();

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

        // Slice 21 Stage 1 (F6): when a page opened from a top-navigation
        // button closes, focus returns to that button. Falls back to the
        // usual Home target when the button is unavailable, and keeps the
        // Movie Details guard FocusFirstAvailableSection already has.
        private void RestoreFocusToNavigationButton(Button navigationButton)
        {
            if (_activeMovieDetails != null)
            {
                _activeMovieDetails.FocusPrimary();
                return;
            }

            if (navigationButton != null &&
                navigationButton.IsVisible &&
                navigationButton.IsEnabled &&
                navigationButton.Focusable &&
                navigationButton.Focus())
            {
                MainScrollViewer.ScrollToVerticalOffset(0);
                return;
            }

            FocusDefaultHomeTarget();
        }

        // Slice 21 Stage 1 (F3 guard): true when any page/overlay host - a
        // visible direct child of the root Grid other than MainScrollViewer
        // and the toast - is currently shown above Home. Same root-Grid
        // structure IsWheelOverOverlay relies on.
        private bool IsAnyOverlayHostVisible()
        {
            DependencyObject container = VisualTreeHelper.GetParent(MainScrollViewer);

            if (container == null)
            {
                return false;
            }

            int count = VisualTreeHelper.GetChildrenCount(container);

            for (int i = 0; i < count; i++)
            {
                UIElement child = VisualTreeHelper.GetChild(container, i) as UIElement;

                if (child == null ||
                    ReferenceEquals(child, MainScrollViewer) ||
                    child is ToastHost)
                {
                    continue;
                }

                if (child.Visibility == Visibility.Visible)
                {
                    return true;
                }
            }

            return false;
        }

        // Slice 21 Stage 1 (F3): true when keyboard focus is not on anything
        // inside this view (nothing focused, the view root itself, or an
        // element outside it), e.g. after an overlay was removed from the
        // tree while it held focus.
        private bool IsHomeFocusLost()
        {
            DependencyObject focused = Keyboard.FocusedElement as DependencyObject;

            if (focused == null || ReferenceEquals(focused, this))
            {
                return true;
            }

            Visual focusedVisual = focused as Visual;

            return focusedVisual != null && !IsAncestorOf(focusedVisual);
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
            // Slice 19B: while Movie Details is open (e.g. after the Source
            // Selector above it closes) focus belongs to Details, never to a
            // hidden Home card.
            if (_activeMovieDetails != null)
            {
                _activeMovieDetails.FocusPrimary();
                return;
            }

            List<MediaSection> populatedSections =
                GetPopulatedSections();

            if (populatedSections.Count == 0)
            {
                // Slice 21 Stage 1 (F3): empty or offline Home has no card to
                // focus; HOME is always present, so keyboard/controller input
                // still has a visible target instead of no focus at all.
                // Not while a page or overlay is still on screen above Home
                // (e.g. Details/Source Selector/Episode Browser closing over
                // a page): HOME is hidden then and must not take focus.
                if (!IsAnyOverlayHostVisible() &&
                    HomeButton != null &&
                    HomeButton.Focus())
                {
                    MainScrollViewer.ScrollToVerticalOffset(0);
                }

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
            // existed and was already wired to the controller's A button,
            // but nothing ever called it for a keyboard Enter/Space press
            // - MediaCard's own PreviewKeyDown only fires when the
            // MediaCard itself is literally the focused element, not when
            // a focused-within descendant or a nav/Hero Button holds
            // focus.
            //
            // Episode Browser hosting/focus fix: this used to call
            // SelectFocusedMediaCard() directly, which (like
            // HandleHomeDirectionalNavigation below) only knows about
            // Home's own sections - it never checked whether an overlay
            // (Episode Browser, Source Selector, Local Media, or a
            // Movies/TV/Anime/Search page) was currently active the way
            // HandleControllerInput's routing already did for a real
            // controller's A button. That meant a keyboard Enter/Space
            // pressed while, say, the Episode Browser was open and
            // waiting for an episode pick fell straight through to Home's
            // card selection instead of reaching the browser at all.
            // Routing through the same RouteButtonToActiveOverlayOrHome
            // used by HandleControllerInput fixes that gap - when nothing
            // is active it still ends up calling SelectFocusedMediaCard()
            // exactly as before, so plain Home behaviour is unchanged.
            if (e.Key == Key.Enter ||
                e.Key == Key.Space)
            {
                // On-screen keyboard task fix: a focused TextBox (e.g.
                // SearchView's SearchBox) needs its own Enter/Space to
                // reach its own KeyDown handler untouched - Space must
                // type a space character and Enter must submit the
                // search, exactly as before this task. Translating
                // either into a synthetic controller A press here, as
                // already done for every other focused control, would
                // instead run whatever ActivateFocusedElement() finds
                // for a focused TextBox (on SearchView, that's now
                // "open the on-screen keyboard"), silently replacing
                // normal text-field behaviour with the wrong action.
                // Letting the key fall through when a TextBox has focus
                // preserves existing text-entry behaviour everywhere
                // unchanged; a real controller's A button is unaffected
                // by this guard, since it never reaches this keyboard-
                // only method at all (it starts at StremioVideoCenter.
                // OnControllerButtonStateChanged and calls
                // HandleControllerInput directly - see that method's
                // own remarks above).
                if (Keyboard.FocusedElement is TextBox)
                {
                    return;
                }

                if (RouteButtonToActiveOverlayOrHome(ControllerInput.A))
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
            // call, so it always falls through to RouteButtonToActive
            // OverlayOrHome exactly as before (see the Episode Browser
            // hosting/focus fix remarks above the Enter/Space branch
            // above - the same gap existed here: this used to call
            // HandleHomeDirectionalNavigation(e.Key) directly, bypassing
            // whatever overlay/page was actually active).
            if (IsControllerEchoEvent(e.Key))
            {
                e.Handled = true;

                return;
            }

            ControllerInput? directionalButton =
                ControllerInputFromDirectionalKey(e.Key);

            if (directionalButton.HasValue &&
                RouteButtonToActiveOverlayOrHome(directionalButton.Value))
            {
                e.Handled = true;
            }
        }

        // Converts a directional Key into the matching D-pad
        // ControllerInput, or null for anything else. The inverse of
        // DirectionalKeyFromControllerInput below - kept as its own
        // method rather than reusing/inverting that one so neither
        // mapping has to guess at the other's intent, exactly as
        // DirectionalKeyFromControllerInput's own remarks describe for
        // why it doesn't need to agree with any other mapping. Used only
        // to let StremioVideoCenterView_PreviewKeyDown's genuine-keyboard-
        // arrow fallback share RouteButtonToActiveOverlayOrHome with the
        // controller path instead of duplicating its overlay-priority
        // logic.
        private static ControllerInput? ControllerInputFromDirectionalKey(
            Key key)
        {
            switch (key)
            {
                case Key.Left:
                    return ControllerInput.DPadLeft;

                case Key.Right:
                    return ControllerInput.DPadRight;

                case Key.Up:
                    return ControllerInput.DPadUp;

                case Key.Down:
                    return ControllerInput.DPadDown;

                default:
                    return null;
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

            // This handler is attached to the view root, so as a tunneling
            // event it runs BEFORE any overlay's own ScrollViewer and used
            // to mark every wheel event handled, scrolling the hidden Home
            // page instead. Episode Browser and Source Selector have their
            // own ScrollViewers: leave the event unhandled so it reaches
            // the one under the pointer.
            if (_activeEpisodeBrowser != null || _activeSourceSelector != null || _activeMovieDetails != null)
            {
                return;
            }

            // Slice 21 Stage 1 (F1): the same applies to every other page or
            // overlay (Movies/TV/Anime, Search, My List, History, Local Media,
            // Profile Selection, the player host). They are siblings of
            // MainScrollViewer in the root Grid, so if the pointer is over
            // one of them it owns the wheel: leave the event unhandled so its
            // own ScrollViewer (if any) receives it, and never scroll the
            // hidden Home page underneath.
            if (IsWheelOverOverlay(e.OriginalSource))
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


        // True when the wheel's original source sits under a direct child of
        // MainScrollViewer's container other than MainScrollViewer itself,
        // i.e. under an overlay/page host. A source outside every child (e.g.
        // a gap on Home itself) is not an overlay, so Home keeps scrolling.
        private bool IsWheelOverOverlay(object originalSource)
        {
            DependencyObject container = VisualTreeHelper.GetParent(MainScrollViewer);

            if (container == null)
            {
                return false;
            }

            DependencyObject current = originalSource as DependencyObject;

            while (current != null)
            {
                DependencyObject parent =
                    (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(current)
                        : LogicalTreeHelper.GetParent(current);

                if (ReferenceEquals(parent, container))
                {
                    return !ReferenceEquals(current, MainScrollViewer);
                }

                current = parent;
            }

            return false;
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

            return RouteButtonToActiveOverlayOrHome(button);
        }

        // =========================================================
        // ROUTE BUTTON TO ACTIVE OVERLAY OR HOME (Episode Browser
        // hosting/focus fix)
        // =========================================================
        //
        // Extracted, unchanged, from what used to be the tail of
        // HandleControllerInput above (the diagnostic logging and the
        // controller-echo marker bookkeeping stay there - both are
        // specific to real hardware controller input and are not part
        // of "which handler owns this button" decision). This is that
        // decision, alone, as its own method, so it can be shared by
        // the keyboard input path too.
        //
        // Root cause this addresses: StremioVideoCenterView_PreviewKeyDown
        // (the keyboard path) used to decide Enter/Space and arrow-key
        // handling entirely on its own, by calling SelectFocusedMediaCard()
        // / HandleHomeDirectionalNavigation() directly - never checking
        // _activeEpisodeBrowser/_activeSourceSelector/_activeLocalMediaView
        // /_activeMoviesPage/_activeTvPage/_activeAnimePage/_activeSearchPage
        // the way this method (then HandleControllerInput's own tail) already
        // did. So a real controller press correctly reached, say, the
        // Episode Browser overlay once it was open, but the identical
        // keyboard key did not - it fell straight through to Home's own
        // card-selection/navigation instead, which is what made the
        // Episode Browser look completely unresponsive to keyboard input
        // even though it had genuinely opened. Routing both input paths
        // through this single method removes that gap without changing
        // what either path does when nothing is active (Home's existing
        // fallback below is identical to before), and without changing
        // any individual overlay's own HandleControllerInput.
        private bool RouteButtonToActiveOverlayOrHome(
            ControllerInput button)
        {
            // Profile Selection (Slice 16A) owns input while shown - the
            // most specific/modal overlay of all, checked before every
            // other overlay below. It can be active before Home has ever
            // loaded (the startup profile-selection gate in
            // StremioVideoCenterView_Loaded), so it must be checked even
            // earlier than the Episode Browser/Source Selector pair that
            // otherwise leads this chain.
            if (_activeProfileSelection != null)
            {
                return _activeProfileSelection.HandleControllerInput(button);
            }

            // Episode browser owns input while season/episode selection
            // is in progress - checked first, since it's the "most
            // specific" overlay when present (mirrors how the source
            // selector is already checked before the player).
            if (_activeEpisodeBrowser != null)
            {
                return _activeEpisodeBrowser.HandleControllerInput(button);
            }

            // Source selector owns input while a selection is in
            // progress - checked before the player, since a selector and
            // a player are never active at the same time, but checking
            // selector first is harmless either way and keeps this
            // consistent with "most specific overlay wins" ordering.
            if (_activeSourceSelector != null)
            {
                return _activeSourceSelector.HandleControllerInput(button);
            }

            // Movie Details (Slice 19B) sits below the Source Selector and
            // above every page it can be opened from. The player is checked
            // after the pages further down, so Details must yield to it
            // explicitly while a movie is playing over it.
            if (_activeMovieDetails != null && _stremioPlayer == null)
            {
                return _activeMovieDetails.HandleControllerInput(button);
            }

            // Local Media owns input while its overlay is open - same
            // "most specific overlay wins" ordering as the other three
            // above.
            // A player started from the Local Files page sits above it: input
            // belongs to the player then (like Movie Details above), and
            // returns to the page once the player is gone.
            if (_activeLocalMediaView != null && _stremioPlayer == null)
            {
                return _activeLocalMediaView.HandleControllerInput(button);
            }

            // Movies/TV/Anime/Search pages (Slice 15) - same "most
            // specific overlay wins" ordering. Only one of these is ever
            // non-null at a time (each Show*PageAsync method is awaited
            // to completion before another can open).
            //
            // These pages (and History/My List below) stay open underneath a
            // player started from them, so - like Movie Details and the Local
            // Files page above - they must yield to the player: otherwise a
            // key/button meant for the player is consumed by the page behind
            // it (the page is never the player's owner while playback is up).
            if (_activeMoviesPage != null && _stremioPlayer == null)
            {
                return _activeMoviesPage.HandleControllerInput(button);
            }

            if (_activeTvPage != null && _stremioPlayer == null)
            {
                return _activeTvPage.HandleControllerInput(button);
            }

            if (_activeAnimePage != null && _stremioPlayer == null)
            {
                return _activeAnimePage.HandleControllerInput(button);
            }

            if (_activeSearchPage != null && _stremioPlayer == null)
            {
                return _activeSearchPage.HandleControllerInput(button);
            }

            // History (Slice 16C) owns input while its overlay is open -
            // same "most specific overlay wins" ordering as the other
            // dedicated pages above (and the same player yield).
            if (_activeHistoryView != null && _stremioPlayer == null)
            {
                return _activeHistoryView.HandleControllerInput(button);
            }

            // My List (Slice 17B) owns input while its overlay is open -
            // same "most specific overlay wins" ordering as History
            // directly above (and the same player yield).
            if (_activeMyListView != null && _stremioPlayer == null)
            {
                return _activeMyListView.HandleControllerInput(button);
            }

            // Player owns input while playback is active.
            if (_stremioPlayer != null)
            {
                return _stremioPlayer.HandleControllerInput(button);
            }

            switch (button)
            {
                case ControllerInput.A:
                    return SelectFocusedMediaCard();

                // Home: Back is a navigation step up the hierarchy (to the
                // Profile Selector), never "close the application".
                case ControllerInput.B:
                    return HandleHomeBack();

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

        // The ONE Back command. Controller B, Esc and Backspace (and the
        // overlays' own Back buttons) all end up in the same routing: the most
        // specific active screen handles it, and only the Profile Selector
        // leaves it unconsumed - which the plugin window turns into closing the
        // application (see NavigationRules.Back).
        public bool HandleBack()
        {
            return RouteButtonToActiveOverlayOrHome(ControllerInput.B);
        }

        private bool _isReturningToProfileSelection;

        // Home -> Back -> Profile Selector, then back to Home with the chosen
        // profile (the same flow as the Switch Profile button).
        private bool HandleHomeBack()
        {
            // While a selection is still being resolved nothing sensible can be
            // backed out of yet: consume the press (do not close anything).
            if (_isResolvingMediaSelection || _isReturningToProfileSelection)
            {
                return true;
            }

            if (NavigationRules.Back(NavScreen.Home, false).Target != NavScreen.ProfileSelector)
            {
                return false;
            }

            var ignored = ReturnToProfileSelectionAsync();
            return true;
        }

        private async Task ReturnToProfileSelectionAsync()
        {
            _isReturningToProfileSelection = true;

            try
            {
                await ShowProfileSelectionAsync(isSwitching: true);

                FocusDefaultHomeTarget();
            }
            finally
            {
                _isReturningToProfileSelection = false;
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
                    return RecoverLostHomeFocus();
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
                    return RecoverLostHomeFocus();
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

        // Slice 21 Stage 1 (F3): a directional press with no section focused
        // used to be ignored, which left an empty/offline Home with no way
        // back onto the page once focus was lost. If focus really is lost
        // (not merely on an element this handler does not manage), put it on
        // HOME and consume the press; otherwise behave exactly as before.
        private bool RecoverLostHomeFocus()
        {
            if (!IsHomeFocusLost())
            {
                return false;
            }

            if (HomeButton != null && HomeButton.Focus())
            {
                MainScrollViewer.ScrollToVerticalOffset(0);
                return true;
            }

            return false;
        }

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

            // Slice 19B: every user-initiated selection (card, Search, My
            // List, History, Hero) starts on All Sources. The provider filter
            // is cleared HERE - the one entry funnel - and not in
            // PlaySelectedMediaAsync, which Movie Details' Watch Now also
            // calls again for the same movie (Source Selector -> Back ->
            // Watch Now) and the episode loop calls again for the same
            // episode; those re-entries are the same content and must keep
            // the choice. A different title/episode is also protected by the
            // content key in ShowSourceSelectorAsync.
            _providerFilterContentKey = null;
            _providerFilterKey = null;

            try
            {
                // Slice 12: only ContentType.TvShow opened the episode
                // browser originally. Slice 15 (Anime Architecture Audit
                // + Kitsu Migration + Provider Role Correction) extends
                // this to ContentType.Anime too, per this slice's own
                // architectural goal ("Anime Series -> Season -> Episode
                // -> Source Selector -> MPV", the same hierarchy as TV) -
                // previously an Anime-typed MediaItem (from either the
                // earlier catalogue rows or the Kitsu browse/search
                // paths) skipped episode browsing entirely and went
                // straight to playback as if it were a single show-level
                // lookup. EpisodeBrowserView.LoadAsync itself already
                // handles all three id shapes this can now reach it with
                // (tt-based via Cinemeta, kitsu-based via Kitsu -
                // see its own remarks). Movies are unaffected either
                // way, since GetContentType never returns TvShow or
                // Anime for them. An item that already carries Season/
                // Episode is treated as already-resolved and skips
                // browsing, going straight to playback.
                // Slice 18B: Continue Watching and History cards for an
                // incomplete Watch State entry resume through one shared
                // pipeline (saved source -> MPV -> saved position). If the
                // entry is no longer resumable (missing/completed), this
                // falls through to the normal path below.
                ResumePlan resumePlan = TryCreateResumePlan(selectedItem);

                if (resumePlan != null)
                {
                    // Immediate feedback for the slow discovery / resolution
                    // that follows. Dismissed on file-loaded or any terminal
                    // path - see ShowResumeLoadingToast. Shown only when the
                    // plan carries a valid saved position (the plan's own
                    // StartPositionSeconds), i.e. playback will really resume.
                    if (resumePlan.StartPositionSeconds.HasValue)
                    {
                        ShowResumeLoadingToast();
                    }

                    await PlaySelectedMediaAsync(
                        BuildResumeMediaItem(resumePlan),
                        resumePlan);

                    return;
                }

                ContentType selectedContentType = GetContentType(selectedItem);
                MediaItem browsedSeries = null;

                // Slice 19B: a movie opens its Movie Details page first;
                // Watch Now there continues into the unchanged source
                // discovery/selection/playback pipeline. Episode-shaped
                // items (already resolved) and TV/Anime keep their paths.
                if (selectedContentType == ContentType.Movie &&
                    !(selectedItem.Season.HasValue && selectedItem.Episode.HasValue))
                {
                    await ShowMovieDetailsAsync(selectedItem);
                    return;
                }

                if ((selectedContentType == ContentType.TvShow || selectedContentType == ContentType.Anime) &&
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

                    browsedSeries = selectedItem;
                    selectedItem = episode;
                }

                await PlaySelectedMediaAsync(
                    selectedItem);

                // Back on an episode's Source Selector returns to the Episode
                // Selector (a movie's returns Home, which is where it already is).
                if (_sourceSelectionCancelled && browsedSeries != null)
                {
                    await BrowseAndPlayEpisodesAsync(browsedSeries);
                }
            }
            finally
            {
                _isResolvingMediaSelection = false;

                // No player exists, so nothing will ever report file-loaded:
                // do not leave the loading toast up.
                if (_resumeLoadingToastActive && _stremioPlayer == null)
                {
                    DismissResumeLoadingToast();
                }
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

            // My List here is series-level: always toggles `show`, never
            // an episode-shaped MediaItem.
            EventHandler onMyListToggleRequested = (sender, args) =>
            {
                bool? nowInList = ToggleMyListAndRefresh(show);

                if (nowInList.HasValue)
                {
                    browser.SetMyListState(nowInList.Value);
                }
            };

            browser.EpisodeSelected += onSelected;
            browser.BrowsingCancelled += onCancelled;
            browser.MyListToggleRequested += onMyListToggleRequested;

            browser.SetMyListState(IsMediaItemInMyList(show));

            _activeEpisodeBrowser = browser;

            EpisodeBrowserHost.Children.Add(browser);
            EpisodeBrowserHost.Visibility = Visibility.Visible;

            // Cinemeta needs no credential and is always constructed.
            var cinemetaService =
                new CinemetaService();

            // Slice 15 Kitsu Migration: needed for a Kitsu-sourced show
            // (StremioId starting "kitsu:") - see EpisodeBrowserView.
            // LoadAsync's own routing. Zero-config, always constructed,
            // exactly like cinemetaService above.
            var kitsuService = new KitsuService();

            await browser.LoadAsync(show, cinemetaService, kitsuService);

            MediaItem result =
                await completionSource.Task;

            browser.EpisodeSelected -= onSelected;
            browser.BrowsingCancelled -= onCancelled;
            browser.MyListToggleRequested -= onMyListToggleRequested;

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
        // SHOW MOVIE DETAILS (Slice 19B)
        // =========================================================
        //
        // Mounts a fresh MovieDetailsView in MovieDetailsHost and returns
        // when the user closes it. Watch Now runs the existing
        // PlaySelectedMediaAsync pipeline for the movie while Details stays
        // mounted beneath the Source Selector/player, so Back from the
        // Source Selector (or the player closing and then Back) uncovers
        // Details. My List goes through the same helpers every other screen
        // uses. The originating screen (Home/Search/My List/History) is
        // never torn down, so closing Details simply uncovers it again, and
        // keyboard focus is restored to the card that opened it.
        private async Task ShowMovieDetailsAsync(MediaItem movie)
        {
            IInputElement previousFocus = Keyboard.FocusedElement;

            var details = new MovieDetailsView();
            var closedSource = new TaskCompletionSource<bool>();

            EventHandler onClosed = (sender, args) =>
            {
                // Ignore Back while sources are being resolved; the
                // discovery in flight would otherwise open the Source
                // Selector over a page the user just left.
                if (_movieDetailsBusy)
                {
                    return;
                }

                closedSource.TrySetResult(true);
            };

            EventHandler onMyListToggleRequested = (sender, args) =>
            {
                bool? nowInList = ToggleMyListAndRefresh(movie);

                if (nowInList.HasValue)
                {
                    details.SetMyListState(nowInList.Value);
                }
            };

            EventHandler onWatchNowRequested = async (sender, args) =>
            {
                if (_movieDetailsBusy)
                {
                    return;
                }

                _movieDetailsBusy = true;
                details.SetBusy(true);

                try
                {
                    await PlaySelectedMediaAsync(movie);
                }
                catch (Exception ex)
                {
                    StremioVideoCenterLogger.Error(
                        "Movie Details: Watch Now failed.",
                        ex);

                    ShowToast("Couldn't load sources. Please try again.");
                }
                finally
                {
                    _movieDetailsBusy = false;
                    details.SetBusy(false);

                    // The Source Selector's My List button may have changed it.
                    details.SetMyListState(IsMediaItemInMyList(movie));
                }
            };

            details.Closed += onClosed;
            details.MyListToggleRequested += onMyListToggleRequested;
            details.WatchNowRequested += onWatchNowRequested;

            details.SetMyListState(IsMediaItemInMyList(movie));

            _activeMovieDetails = details;

            MovieDetailsHost.Children.Add(details);
            MovieDetailsHost.Visibility = Visibility.Visible;

            // Focus once the control is laid out; the metadata fetch below
            // never delays it.
            details.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (ReferenceEquals(_activeMovieDetails, details))
                    {
                        details.FocusPrimary();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // Fail-soft (CinemetaService.GetMetaAsync never throws); not
            // awaited so the page is interactive immediately.
            Task loadTask = details.LoadAsync(movie, new CinemetaService());

            await closedSource.Task;

            details.Closed -= onClosed;
            details.MyListToggleRequested -= onMyListToggleRequested;
            details.WatchNowRequested -= onWatchNowRequested;

            MovieDetailsHost.Children.Remove(details);
            MovieDetailsHost.Visibility = Visibility.Collapsed;

            if (ReferenceEquals(_activeMovieDetails, details))
            {
                _activeMovieDetails = null;
            }

            RestoreFocusAfterMovieDetails(previousFocus);
        }

        // Returns focus to the card/button that opened Details when it is
        // still on screen (Search results, My List, History, a Home row,
        // the Hero button); otherwise falls back to the usual Home focus.
        private void RestoreFocusAfterMovieDetails(IInputElement previousFocus)
        {
            UIElement previous = previousFocus as UIElement;

            if (previous != null && previous.IsVisible && previous.Focusable)
            {
                Keyboard.Focus(previous);

                if (ReferenceEquals(Keyboard.FocusedElement, previous))
                {
                    return;
                }
            }

            FocusFirstAvailableSection();
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
        // Torrentio/Comet configuration - it depends on none of them.
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
        // =========================================================
        // PROFILE SELECTION (Slice 16A - Profile Foundation)
        // =========================================================
        //
        // Shared by BOTH the startup gate (StremioVideoCenterView_Loaded,
        // isSwitching: false) and SWITCH PROFILE (isSwitching: true) - one
        // implementation, per the Slice 16 spec's explicit requirement.
        // Same overlay-host pattern as ShowLocalMediaAsync above, but
        // deliberately has no "cancelled" path - ProfileSelectionView
        // itself guarantees the user always leaves by selecting a profile
        // (see that view's own remarks), so this always returns a real
        // Guid, never null.
        private async Task<Guid?> ShowProfileSelectionAsync(bool isSwitching)
        {
            if (_profileService == null)
            {
                // Defensive only - see _profileService's own remarks.
                // Nothing to show; callers proceed exactly as if the
                // user had already selected the (nonexistent) profile.
                return null;
            }

            var profileSelectionView = new ProfileSelectionView();
            profileSelectionView.Initialize(_profileService, isSwitching);

            var completionSource = new TaskCompletionSource<Guid>();

            EventHandler<Guid> onSelected = null;

            onSelected = (sender, profileId) =>
            {
                completionSource.TrySetResult(profileId);
            };

            profileSelectionView.ProfileSelected += onSelected;

            _activeProfileSelection = profileSelectionView;

            ProfileSelectionHost.Children.Add(profileSelectionView);
            ProfileSelectionHost.Visibility = Visibility.Visible;

            profileSelectionView.Focus();
            profileSelectionView.FocusFirstTile();

            Guid selectedProfileId = await completionSource.Task;

            profileSelectionView.ProfileSelected -= onSelected;

            ProfileSelectionHost.Children.Remove(profileSelectionView);
            ProfileSelectionHost.Visibility = Visibility.Collapsed;

            _activeProfileSelection = null;

            _profileService.SetActiveProfile(selectedProfileId);

            OnActiveProfileChanged();

            return selectedProfileId;
        }

        // =========================================================
        // ACTIVE PROFILE CHANGED HOOK
        // =========================================================
        //
        // Slice 16C: now refreshes Continue Watching from Watch State for
        // whichever profile just became active - the single wiring point
        // called from both the startup gate and SWITCH PROFILE (see
        // ShowProfileSelectionAsync above), satisfying "no stale profile
        // data after switching" for the one Home section Slice 16C makes
        // real. History has no Home-section presence to refresh here -
        // it is loaded fresh every time ShowHistoryAsync opens the
        // overlay instead (see that method).
        private void OnActiveProfileChanged()
        {
            RefreshContinueWatching();

            // Slice 17B: My List is profile-scoped exactly like Continue
            // Watching above - both the Home row and the Hero My List
            // button must reflect the newly-active profile's membership,
            // never the previous profile's stale state.
            RefreshMyList();
            RefreshHeroMyListButtonState();

            // Slice 17C: the previous profile's recommendations must vanish
            // immediately and can never land after this switch.
            RefreshRecommendations(clearImmediately: true);
        }

        // =========================================================
        // RECOMMENDED FOR YOU + TOAST (Slice 17C)
        // =========================================================

        private void ShowToast(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                // This toast replaces the (single-slot) resume-loading one.
                _resumeLoadingToastActive = false;
            }

            FeedbackToast?.Show(message);
        }

        // Slice 20 Stage 2: playback errors are shown through the same
        // single-slot, non-focusable, non-hit-testable toast as other
        // feedback (never a modal MessageBox). It shows for longer than a
        // confirmation because it is a sentence to read, and being
        // single-slot a new notice replaces the old one - they cannot stack.
        private const double PlaybackNoticeSeconds = 6;

        private void ShowPlaybackNotice(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                // This notice replaces the (single-slot) resume-loading toast.
                _resumeLoadingToastActive = false;
            }

            FeedbackToast?.Show(message, PlaybackNoticeSeconds);
        }

        // Loading toast for a Continue Watching / History item that is being
        // resumed from saved Watch State (UI only). It uses the same
        // single-slot FeedbackToast, so it can never stack with another
        // message; any other message simply replaces it (see ShowToast /
        // ShowPlaybackNotice). It is shown when the resume is requested and
        // dismissed when mpv reports file-loaded (StremioPlayer_
        // PlaybackEstablished) or on any path where playback will not
        // proceed (source selector shown, ClosePlayer, start failure, the
        // selection handler finishing with no player). The long timer is
        // only a failsafe for the toast's own auto-hide.
        private const double ResumeLoadingToastFailsafeSeconds = 120;
        private bool _resumeLoadingToastActive;

        private void ShowResumeLoadingToast()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowResumeLoadingToast));
                return;
            }

            if (FeedbackToast == null)
            {
                return;
            }

            FeedbackToast.Show("Loading content\u2026", ResumeLoadingToastFailsafeSeconds);
            _resumeLoadingToastActive = true;
        }

        private void DismissResumeLoadingToast()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(DismissResumeLoadingToast));
                return;
            }

            if (!_resumeLoadingToastActive)
            {
                return;
            }

            _resumeLoadingToastActive = false;
            FeedbackToast?.Hide();
        }

        private const int RecommendationDebounceMilliseconds = 400;

        private PersonalisationService _personalisationService;
        private AnimeGenreEnrichmentService _genreEnrichmentService;
        private CatalogueService _recommendationCatalogue;
        private CancellationTokenSource _recommendationCts;
        private int _recommendationVersion;

        // Recalculates the personalised row for the ACTIVE profile. Called
        // only on profile change, My List change, Hero load and player
        // close - never on focus/navigation. Every call cancels the
        // previous one and a short delay coalesces bursts. Results are
        // applied only if this call is still the newest AND the active
        // profile is still the one it started for, so a previous profile's
        // results can never appear under another profile. Personalisation
        // failures are swallowed: the row simply stays hidden.
        private async void RefreshRecommendations(bool clearImmediately)
        {
            HomeViewModel vm = DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            CancellationTokenSource previous = _recommendationCts;

            if (previous != null)
            {
                previous.Cancel();
            }

            var cts = new CancellationTokenSource();
            _recommendationCts = cts;
            int version = ++_recommendationVersion;

            if (clearImmediately)
            {
                vm.SetRecommended(null);
            }

            Guid? profileId = _profileService?.GetActiveProfileId();

            if (!profileId.HasValue || _myListService == null)
            {
                vm.SetRecommended(null);
                return;
            }

            try
            {
                await Task.Delay(RecommendationDebounceMilliseconds, cts.Token);

                if (_personalisationService == null)
                {
                    _personalisationService = new PersonalisationService(
                        _myListService,
                        _watchHistoryService,
                        _watchStateService);
                }

                // Cinemeta (Movie/TV) and Kitsu (Anime) only. The
                // catalogue cache is static, so this shares Home's cache.
                if (_recommendationCatalogue == null)
                {
                    _recommendationCatalogue = new CatalogueService(
                        new CinemetaService(),
                        new KitsuService());
                }

                // Slice 17C: one-off, batched back-fill of genres for this
                // profile's pre-existing Anime My List/History entries (only
                // runs when such entries exist; each id is requested at most
                // once per app session). Cancelled with the refresh.
                if (_genreEnrichmentService == null)
                {
                    var kitsuForEnrichment = new KitsuService();

                    _genreEnrichmentService = new AnimeGenreEnrichmentService(
                        _myListService,
                        _watchHistoryService,
                        kitsuForEnrichment.GetCategoryTitlesByIdsAsync);
                }

                await _genreEnrichmentService.EnrichAsync(profileId.Value, cts.Token);

                cts.Token.ThrowIfCancellationRequested();

                TasteProfile taste =
                    _personalisationService.BuildTasteProfile(profileId.Value, DateTime.UtcNow);

                if (!taste.HasEnoughSignal)
                {
                    vm.SetRecommended(null);
                    return;
                }

                List<MediaItem> items =
                    await _personalisationService.GetRecommendationsAsync(
                        taste,
                        _recommendationCatalogue,
                        BuildHeroKeySet(vm),
                        cts.Token);

                if (cts.IsCancellationRequested ||
                    version != _recommendationVersion ||
                    _profileService?.GetActiveProfileId() != profileId)
                {
                    return;
                }

                // Hero may have changed while the request was in flight.
                HashSet<string> heroKeys = BuildHeroKeySet(vm);

                vm.SetRecommended(
                    items.Where(item => !heroKeys.Contains(item.StremioId)));
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer refresh or a profile switch.
            }
            catch (Exception ex)
            {
                if (version == _recommendationVersion)
                {
                    vm.SetRecommended(null);
                }

                StremioVideoCenterLogger.Info(
                    "Recommendations unavailable: " + ex.GetType().Name);
            }
        }

        private static HashSet<string> BuildHeroKeySet(HomeViewModel vm)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HeroItem hero in vm.HeroItems)
            {
                if (hero != null && !string.IsNullOrWhiteSpace(hero.StremioId))
                {
                    keys.Add(hero.StremioId);
                }
            }

            return keys;
        }

        // Rebuilds HomeViewModel.ContinueWatching from WatchStateService's
        // current incomplete entries for the active profile - see
        // HomeViewModel.LoadContinueWatching's own remarks for why this is
        // always a full reconstruction rather than an incremental update.
        private void RefreshContinueWatching()
        {
            HomeViewModel vm = DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (_watchStateService == null || !activeProfileId.HasValue)
            {
                vm.ContinueWatching.Clear();
                return;
            }

            vm.LoadContinueWatching(_watchStateService, activeProfileId.Value);
        }

        // 16A fix #4 (initial Home focus): after SWITCH PROFILE resolves,
        // focus must land on HomeButton - the same "HOME in the Hero
        // navigation receives initial focus" rule the startup profile-
        // selection gate already satisfies via FocusDefaultHomeTarget()
        // (see that method's own remarks). This previously called
        // FocusFirstAvailableSection() directly, which is why testing
        // found SWITCH PROFILE landing in the Hero/content area instead of
        // on HOME.
        private async void SwitchProfileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowProfileSelectionAsync(isSwitching: true);

            FocusDefaultHomeTarget();
        }

        private async Task ShowLocalMediaAsync()
        {
            var localMediaView = new LocalMediaView();

            localMediaView.Initialize(_localLibrary);

            var completionSource =
                new TaskCompletionSource<bool>();

            EventHandler onClosed = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            // Local Files hands playback and details back to this view so
            // they run through the existing pipeline (nothing in the page
            // plays media itself).
            EventHandler<MediaItemSelectedEventArgs> onPlay =
                async (sender, args) => await HandleLocalPlayAsync(args.Item);

            EventHandler<MediaItemSelectedEventArgs> onDetails =
                async (sender, args) => await HandleLocalDetailsAsync(args.Item);

            localMediaView.Closed += onClosed;
            localMediaView.PlayRequested += onPlay;
            localMediaView.DetailsRequested += onDetails;

            _activeLocalMediaView = localMediaView;

            LocalMediaHost.Children.Add(localMediaView);
            LocalMediaHost.Visibility = Visibility.Visible;

            localMediaView.Focus();

            await completionSource.Task;

            localMediaView.Closed -= onClosed;
            localMediaView.PlayRequested -= onPlay;
            localMediaView.DetailsRequested -= onDetails;

            LocalMediaHost.Children.Remove(localMediaView);
            LocalMediaHost.Visibility = Visibility.Collapsed;

            _activeLocalMediaView = null;

            RestoreFocusToNavigationButton(LocalMediaButton);
        }

        // =========================================================
        // LOCAL FILES: PLAY / DETAILS HAND-OFF
        // =========================================================
        //
        // "Play" on a Local Files card: an explicit user action that plays
        // THAT local file through the normal pipeline (PlaySelectedMediaAsync),
        // resuming from Watch State when the item has an unfinished entry.
        // The item carries LocalKey, which tells PlaySelectedMediaAsync to
        // play the local file directly instead of discovering streams.
        private async Task HandleLocalPlayAsync(MediaItem localItem)
        {
            if (localItem == null || _isResolvingMediaSelection)
            {
                return;
            }

            _isResolvingMediaSelection = true;

            try
            {
                _providerFilterContentKey = null;
                _providerFilterKey = null;

                // The same canonical key Watch State uses (catalogue key for
                // a matched file, the path-free local id for an unmatched
                // one), so progress is shared with a streaming copy.
                LocalLibraryItem indexed = _localLibrary != null && !string.IsNullOrEmpty(localItem.LocalKey)
                    ? _localLibrary.GetItem(localItem.LocalKey)
                    : null;

                if (indexed != null)
                {
                    localItem.ResumeCanonicalKey = indexed.CanonicalKey;
                }

                ResumePlan resumePlan = TryCreateResumePlan(localItem);

                if (resumePlan != null)
                {
                    MediaItem resumeItem = BuildResumeMediaItem(resumePlan);
                    resumeItem.LocalKey = localItem.LocalKey;

                    await PlaySelectedMediaAsync(resumeItem, resumePlan);
                    return;
                }

                await PlaySelectedMediaAsync(localItem);
            }
            finally
            {
                _isResolvingMediaSelection = false;
            }
        }

        // "Open details" for a matched Local Files card: the existing Movie
        // Details / Episode Browser for the catalogue title, through the same
        // selection funnel as every other card.
        private async Task HandleLocalDetailsAsync(MediaItem catalogueItem)
        {
            if (catalogueItem == null)
            {
                return;
            }

            await HandleMediaSelectedAsync(catalogueItem);
        }

        private async void LocalMediaButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowLocalMediaAsync();
        }

        // =========================================================
        // HISTORY (Slice 16C - Continue Watching + History)
        // =========================================================
        //
        // Same overlay-host pattern as ShowLocalMediaAsync above. Builds
        // display MediaItems from WatchHistoryService's series/movie-level
        // entries for the active profile - History never routes through
        // HomeViewModel (it has no Home-section presence, unlike Continue
        // Watching), it is loaded fresh on every visit instead.
        //
        // A history entry becomes a MediaItem carrying only its
        // series/movie-level identity (StremioId = entry.CanonicalKey,
        // no Season/Episode) - selecting a movie entry therefore plays it
        // directly, and selecting a TV/Kitsu entry opens the Episode
        // Browser exactly like picking that show from a catalogue row
        // would (HandleMediaSelectedAsync's existing "no Season/Episode
        // yet -> browse" path), never jumping straight back into the
        // latest-watched episode - that would be next-episode/resume
        // inference, explicitly out of scope for this slice (Slice 18).
        // The History -> Watch State relationship required by the spec is
        // satisfied by the lookup below (entry.LatestWatchedCanonicalKey
        // resolved against _watchStateService) without building any
        // resume UI from it.
        private async Task ShowHistoryAsync()
        {
            var historyView = new HistoryView();

            List<MediaItem> historyDisplayItems = BuildHistoryDisplayItems();

            historyView.Initialize(historyDisplayItems);

            var completionSource =
                new TaskCompletionSource<bool>();

            EventHandler onClosed = null;
            EventHandler<MediaItemSelectedEventArgs> onMediaSelected = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            onMediaSelected = async (sender, args) =>
            {
                await HandleMediaSelectedAsync(args.Item);
            };

            historyView.Closed += onClosed;
            historyView.MediaSelected += onMediaSelected;

            _activeHistoryView = historyView;

            HistoryHost.Children.Add(historyView);
            HistoryHost.Visibility = Visibility.Visible;

            historyView.Focus();

            await completionSource.Task;

            historyView.Closed -= onClosed;
            historyView.MediaSelected -= onMediaSelected;

            HistoryHost.Children.Remove(historyView);
            HistoryHost.Visibility = Visibility.Collapsed;

            _activeHistoryView = null;

            RestoreFocusToNavigationButton(HistoryButton);
        }

        // Builds the display MediaItems HistoryView shows, from
        // WatchHistoryService's entries for the active profile - denormalised
        // metadata only, no network calls (Slice 16C spec, Section 27).
        private List<MediaItem> BuildHistoryDisplayItems()
        {
            var displayItems = new List<MediaItem>();

            if (_watchHistoryService == null)
            {
                return displayItems;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (!activeProfileId.HasValue)
            {
                return displayItems;
            }

            IReadOnlyList<WatchHistoryEntry> rawEntries =
                _watchHistoryService.GetEntriesForProfile(activeProfileId.Value);

            foreach (WatchHistoryEntry entry in rawEntries)
            {
                if (entry == null)
                {
                    continue;
                }

                // History -> Watch State lookup relationship (Slice 16C
                // spec, Section 17) - resolved here so the relationship
                // demonstrably exists, without building any resume UI
                // from it (that's Slice 18).
                WatchStateEntry latestWatchState = _watchStateService?.GetEntry(
                    activeProfileId.Value,
                    entry.LatestWatchedCanonicalKey);

                // Slice 18B: an INCOMPLETE latest Watch State keeps its
                // progress bar here even after it has fallen out of the
                // latest-25 Continue Watching window, and selecting the
                // card resumes it through the same pipeline Continue
                // Watching uses. A completed (or missing) latest state
                // shows no bar and keeps the old behaviour (movie plays,
                // series opens the Episode Browser).
                bool incomplete =
                    latestWatchState != null &&
                    !latestWatchState.IsCompleted &&
                    !string.IsNullOrWhiteSpace(latestWatchState.CanonicalKey);

                displayItems.Add(new MediaItem
                {
                    // Series/movie-level identity only - no Season/Episode -
                    // see method remarks above for why.
                    StremioId = entry.CanonicalKey,
                    Title = entry.Title,
                    PosterPath = entry.PosterPath,
                    Year = entry.Year,
                    Type = entry.Type,
                    Progress = incomplete ? PlaybackProgress.ForEntry(latestWatchState) : 0,
                    ResumeCanonicalKey = incomplete ? latestWatchState.CanonicalKey : null
                });
            }

            return displayItems;
        }

        private async void HistoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowHistoryAsync();
        }

        // =========================================================
        // MY LIST (Slice 17B - My List UI + Hero Integration)
        // =========================================================
        //
        // Same overlay-host pattern as ShowHistoryAsync above. Builds
        // display MediaItems from MyListService's entries for the active
        // profile - My List never routes playback itself; selecting a
        // card goes through the same HandleMediaSelectedAsync every other
        // catalogue card already uses, so a Movie entry plays directly
        // and a TV/Anime entry opens the Episode Browser exactly like
        // picking that show from a catalogue row would.
        //
        // Removing an entry (MyListView.EntryRemoveRequested) updates the
        // underlying MyListService immediately, then refreshes both the
        // open MyListView itself (so the grid/empty-state/focus update
        // without closing the overlay) and the Home row / Hero button
        // (RefreshMyList/RefreshHeroMyListButtonState) so nothing is left
        // stale once the user eventually returns to Home or the Hero.
        private async Task ShowMyListAsync()
        {
            var myListView = new MyListView();

            myListView.Initialize(BuildMyListDisplayEntries());

            var completionSource =
                new TaskCompletionSource<bool>();

            EventHandler onClosed = null;
            EventHandler<MediaItemSelectedEventArgs> onMediaSelected = null;
            EventHandler<MyListEntryRemovedEventArgs> onEntryRemoveRequested = null;

            onClosed = (sender, args) =>
            {
                completionSource.TrySetResult(true);
            };

            onMediaSelected = async (sender, args) =>
            {
                await HandleMediaSelectedAsync(args.Item);
            };

            onEntryRemoveRequested = (sender, args) =>
            {
                Guid? activeProfileId = _profileService?.GetActiveProfileId();

                if (_myListService == null || !activeProfileId.HasValue || args == null)
                {
                    return;
                }

                bool existed = _myListService.IsInMyList(activeProfileId.Value, args.CanonicalKey);

                _myListService.Remove(activeProfileId.Value, args.CanonicalKey);

                RefreshMyList();
                RefreshHeroMyListButtonState();
                RefreshRecommendations(clearImmediately: false);

                if (existed)
                {
                    ShowToast("Removed from My List");
                }

                myListView.UpdateEntries(BuildMyListDisplayEntries());
            };

            myListView.Closed += onClosed;
            myListView.MediaSelected += onMediaSelected;
            myListView.EntryRemoveRequested += onEntryRemoveRequested;

            _activeMyListView = myListView;

            MyListHost.Children.Add(myListView);
            MyListHost.Visibility = Visibility.Visible;

            myListView.Focus();

            await completionSource.Task;

            myListView.Closed -= onClosed;
            myListView.MediaSelected -= onMediaSelected;
            myListView.EntryRemoveRequested -= onEntryRemoveRequested;

            MyListHost.Children.Remove(myListView);
            MyListHost.Visibility = Visibility.Collapsed;

            _activeMyListView = null;

            RestoreFocusToNavigationButton(NavigationMyListButton);
        }

        // Builds the display MediaItems MyListView shows, from
        // MyListService's entries for the active profile - denormalised
        // metadata only, no network calls (same discipline as
        // BuildHistoryDisplayItems above).
        private List<MyListEntry> BuildMyListDisplayEntries()
        {
            var entries = new List<MyListEntry>();

            if (_myListService == null)
            {
                return entries;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (!activeProfileId.HasValue)
            {
                return entries;
            }

            entries.AddRange(_myListService.GetEntriesForProfile(activeProfileId.Value));

            return entries;
        }

        private async void NavigationMyListButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ShowMyListAsync();
        }

        // Rebuilds HomeViewModel.MyList from MyListService's current
        // entries for the active profile - see HomeViewModel.LoadMyList's
        // own remarks. Same shape as RefreshContinueWatching above, called
        // from OnActiveProfileChanged and after any My List mutation
        // (Source Selector toggle, Hero toggle, My List page removal) so
        // the Home row never shows stale membership.
        private void RefreshMyList()
        {
            HomeViewModel vm = DataContext as HomeViewModel;

            if (vm == null)
            {
                return;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (_myListService == null || !activeProfileId.HasValue)
            {
                vm.MyList.Clear();
                return;
            }

            vm.LoadMyList(_myListService, activeProfileId.Value);
        }

        // =========================================================
        // PLAY SELECTED MEDIA
        // =========================================================

        // Slice 18B: reads Watch State for the ACTIVE profile and returns a
        // resume plan, or null when the selected card is not a resumable
        // (incomplete) item.
        private ResumePlan TryCreateResumePlan(MediaItem selectedItem)
        {
            if (selectedItem == null ||
                string.IsNullOrWhiteSpace(selectedItem.ResumeCanonicalKey) ||
                _watchStateService == null)
            {
                return null;
            }

            Guid? profileId = _profileService?.GetActiveProfileId();

            if (!profileId.HasValue)
            {
                return null;
            }

            return ResumePlanner.Create(
                _watchStateService,
                profileId.Value,
                selectedItem.ResumeCanonicalKey);
        }

        // The playback MediaItem for a resumed entry, rebuilt from Watch
        // State's denormalised metadata in the same shape the Episode
        // Browser produces (show-level StremioId + Season/Episode), so
        // Watch State/History establishment behaves exactly as for a
        // normal play.
        private static MediaItem BuildResumeMediaItem(ResumePlan plan)
        {
            WatchStateEntry entry = plan.Entry;

            bool isEpisode = !string.IsNullOrWhiteSpace(entry.ShowStremioId);
            bool isKitsu =
                isEpisode &&
                entry.ShowStremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);

            return new MediaItem
            {
                StremioId = isEpisode ? entry.ShowStremioId : entry.CanonicalKey,
                Title = entry.Title,
                PosterPath = entry.PosterPath,
                Year = entry.Year,
                Type = entry.Type,
                // Kitsu entries store no season (absolute episode numbering);
                // a placeholder keeps the item episode-shaped.
                // EstablishWatchStateOnPlaybackStart still stores null for it.
                Season = isEpisode ? (entry.SeasonNumber ?? (isKitsu ? 1 : (int?)null)) : null,
                Episode = isEpisode ? entry.EpisodeNumber : null
            };
        }

        // =========================================================
        // EPISODE NAVIGATION (Slice 18D)
        // =========================================================
        //
        // Previous / Next / auto-next change the episode INSIDE the running
        // player: the target's streams are resolved through the normal
        // aggregation pipeline, its own Watch State / saved source / position
        // are used through the 18B ResumePlanner, and the same MpvPlayer and
        // mpv process load the new file (loadfile replace) under a NEW
        // PlaybackContext. Episode identity is always the target's canonical
        // key from the episode list.

        private EpisodeListService _episodeListService;
        private bool _isEpisodeTransitioning;
        private int _playbackGeneration;

        private EpisodeListService GetEpisodeListService()
        {
            if (_episodeListService == null)
            {
                _episodeListService = new EpisodeListService(new CinemetaService(), new KitsuService());
            }

            return _episodeListService;
        }

        private static bool IsEpisodeShaped(MediaItem item)
        {
            return item != null &&
                   item.Episode.HasValue &&
                   !string.IsNullOrWhiteSpace(item.StremioId);
        }

        // Attaches neighbours when the list is already cached. No network.
        private void AttachCachedEpisodeContext(PlaybackContext context)
        {
            if (context == null || context.Episode != null || !IsEpisodeShaped(context.MediaItem))
            {
                return;
            }

            // An unmatched local series has no online episode list; its order
            // comes from the library itself.
            EpisodeList list = LocalLibraryManager.IsLocalShowKey(context.MediaItem.StremioId)
                ? (_localLibrary != null ? _localLibrary.BuildLocalEpisodeList(context.MediaItem.StremioId) : null)
                : GetEpisodeListService().TryGetCached(context.MediaItem.StremioId);

            if (list != null)
            {
                context.AttachEpisode(
                    EpisodeContext.Create(list, context.CanonicalKey, context.MediaItem.Title));
            }
        }

        // Loads the series' episode list in the background and, if this is
        // still the current attempt, shows Previous/Next. Fail-soft: without a
        // list the player simply has no episode navigation.
        private async Task AttachEpisodeNavigationAsync(PlaybackContext context)
        {
            if (!IsEpisodeShaped(context.MediaItem))
            {
                return;
            }

            EpisodeList list = null;

            try
            {
                list = LocalLibraryManager.IsLocalShowKey(context.MediaItem.StremioId)
                    ? (_localLibrary != null ? _localLibrary.BuildLocalEpisodeList(context.MediaItem.StremioId) : null)
                    : await GetEpisodeListService().GetAsync(context.MediaItem.StremioId);
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Episode list unavailable: " + UserFacingErrors.ForLog(ex));
            }

            // Offline fallback, local playback only: when the online list
            // could not be loaded, a locally played episode still gets
            // Previous / Next from the files in the library.
            if (list == null &&
                _localLibrary != null &&
                context.Source != null &&
                context.Source.Kind == PlaybackSourceKind.Local)
            {
                list = _localLibrary.BuildLocalEpisodeList(context.MediaItem.StremioId);
            }

            try
            {
                if (list == null || _playbackContext != context || _stremioPlayer == null)
                {
                    return;
                }

                EpisodeContext episode =
                    EpisodeContext.Create(list, context.CanonicalKey, context.MediaItem.Title);

                if (episode != null)
                {
                    context.AttachEpisode(episode);
                    _stremioPlayer.SetEpisodeNavigation(episode);
                }
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Episode list unavailable: " + UserFacingErrors.ForLog(ex));
            }
        }

        private void StremioPlayer_PreviousEpisodeRequested(object sender, EventArgs e)
        {
            if (sender == _stremioPlayer && !_isSwappingPlayerForSelector)
            {
                var ignored = NavigateEpisodeAsync(forward: false, automatic: false);
            }
        }

        private void StremioPlayer_NextEpisodeRequested(object sender, EventArgs e)
        {
            if (sender == _stremioPlayer && !_isSwappingPlayerForSelector)
            {
                var ignored = NavigateEpisodeAsync(forward: true, automatic: false);
            }
        }

        // mpv reported a genuine end of file (never error/stop/quit/exit).
        // 1. Mark exactly this attempt's entry completed and persist it.
        // 2. If a next episode exists, play it in the same player.
        private void StremioPlayer_EndOfFileReached(object sender, EventArgs e)
        {
            if (sender != _stremioPlayer || _stremioPlayer == null || _isSwappingPlayerForSelector)
            {
                return;
            }

            PlaybackContext context = _playbackContext;
            PlaybackState state = _stremioPlayer.CurrentPlaybackState;

            if (context == null || state == null ||
                !ReferenceEquals(_stremioPlayer.CurrentContext, context))
            {
                return;
            }

            // Re-checked against live state: a stale end-of-file signal from a
            // file that has since been replaced must not complete anything.
            bool genuineEnd = state.ReachedEndOfFile && state.IsPlaybackEstablished;

            if (genuineEnd && _progressRecorder != null)
            {
                _progressRecorder.Complete(state);
                RefreshContinueWatching();
            }

            if (AutoNextPolicy.ShouldAutoNext(
                    genuineEnd,
                    state.IsPlaybackEstablished,
                    context.Episode != null && context.Episode.HasNext,
                    _isEpisodeTransitioning,
                    playerClosing: false))
            {
                var ignored = NavigateEpisodeAsync(forward: true, automatic: true);
            }
        }

        private async Task NavigateEpisodeAsync(bool forward, bool automatic)
        {
            PlaybackContext current = _playbackContext;
            EpisodeContext episode = current != null ? current.Episode : null;

            if (_stremioPlayer == null || episode == null || _isEpisodeTransitioning)
            {
                return;
            }

            EpisodeRef target = forward ? episode.Next : episode.Previous;

            if (target == null)
            {
                return;
            }

            _isEpisodeTransitioning = true;

            try
            {
                _stremioPlayer.ShowNotice(
                    (forward ? "Next episode" : "Previous episode") +
                    "\u2026  " + EpisodeContext.FormatLabel(target));

                MediaItem targetItem = episode.BuildMediaItem(target, current.MediaItem);

                // The target's OWN Watch State (same profile as the current
                // attempt): incomplete -> its saved position and saved source;
                // none/completed -> start from the beginning.
                ResumePlan plan = ResumePlanner.Create(
                    _watchStateService,
                    current.ProfileId,
                    target.CanonicalKey);

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "Episode {0}: target={1} resume={2} savedSource={3}",
                        automatic ? "auto-next" : (forward ? "next" : "previous"),
                        target.CanonicalKey,
                        plan != null && plan.StartPositionSeconds.HasValue,
                        plan != null && plan.SavedSource != null));

                await PlaySelectedMediaAsync(
                    targetItem,
                    plan,
                    new EpisodeTransition
                    {
                        ProfileId = current.ProfileId,
                        CanonicalKey = target.CanonicalKey,
                        Generation = _playbackGeneration,
                        PreferredSource = current.BuildSavedSource()
                    });

                // The target episode needed a Source Selector and the user
                // backed out of it: back to the Episode Selector.
                if (_sourceSelectionCancelled)
                {
                    await BrowseAndPlayEpisodesAsync(BuildSeriesItem(current));
                }
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Episode navigation failed: " + UserFacingErrors.ForLog(ex));
            }
            finally
            {
                _isEpisodeTransitioning = false;
            }
        }

        // Describes an in-player episode change (Previous / Next / auto-next).
        // The player stays open and is reused; everything about the TARGET
        // episode is resolved through the normal pipeline.
        private sealed class EpisodeTransition
        {
            public Guid ProfileId;

            // The target episode's canonical key (used verbatim as the request id).
            public string CanonicalKey;

            // _playbackGeneration when the transition began; a different value
            // later means the player was closed/replaced meanwhile.
            public int Generation;

            // The source that played the episode being left, used only as a
            // "same addon + same stream label" hint when the target episode has
            // no saved source of its own.
            public SavedSourceReference PreferredSource;
        }

        private async Task PlaySelectedMediaAsync(
            MediaItem mediaItem,
            ResumePlan resumePlan = null,
            EpisodeTransition transition = null)
        {

            // Set again below only if the user backs out of the Source Selector.
            _sourceSelectionCancelled = false;

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

                ShowPlaybackNotice(
                    "This title can't be played yet because it has no source identifier.");

                return;
            }

            // Defensive safety net: a stale player from a previous session
            // should never still be around by the time a new play attempt
            // starts (the home grid isn't reachable while PlaybackTestHost
            // covers the screen), but this guarantees it rather than
            // relying on that reachability assumption alone.
            // (An episode transition deliberately keeps the running player.)
            if (_stremioPlayer != null && transition == null)
            {
                // ClosePlayer dismisses the resume-loading toast; this attempt
                // is still loading, so restore it.
                bool loadingToastWasActive = _resumeLoadingToastActive;

                ClosePlayer();

                if (loadingToastWasActive)
                {
                    ShowResumeLoadingToast();
                }
            }

            try
            {
                // (Built by CreateStreamingServices so source-failure recovery
                // can reuse exactly the same registry / Real-Debrid wiring.)
                PlaybackSourceAggregationService aggregator;
                PlaybackSourceNormalizationService normalizer;
                CreateStreamingServices(out aggregator, out normalizer);

                var request = new ContentRequest
                {
                    Type = resumePlan != null ? resumePlan.ContentType : GetContentType(mediaItem),
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

                if (resumePlan != null)
                {
                    // Slice 18B: the Watch State entry's own canonical key IS
                    // the identity (movie "tt…", episode "tt…:S:E", Kitsu
                    // "kitsu:id:episode") - used verbatim, never recomputed,
                    // so resume can only ever touch this exact episode.
                    request.ExplicitStremioId = resumePlan.CanonicalKey;
                }
                else if (transition != null)
                {
                    // Slice 18D: the target episode's canonical key from the
                    // episode list - the same "tt:S:E" / "kitsu:id:episode"
                    // shape, used verbatim.
                    request.ExplicitStremioId = transition.CanonicalKey;
                }
                else if (mediaItem.Season.HasValue &&
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

                // Local Files: a card played from the Local Files page names
                // its library item (LocalKey). Its identity is the item's own
                // canonical key - the shared catalogue key for a matched file
                // (so progress is shared with a streaming copy), or the
                // path-free local id for an unmatched one - used verbatim.
                // Streaming items (no LocalKey) never reach this.
                if (resumePlan == null && transition == null && !string.IsNullOrEmpty(mediaItem.LocalKey))
                {
                    LocalLibraryItem localIndexed = _localLibrary != null
                        ? _localLibrary.GetItem(mediaItem.LocalKey)
                        : null;

                    if (localIndexed != null)
                    {
                        request.ImdbId = null;
                        request.Season = null;
                        request.Episode = null;
                        request.ExplicitStremioId = localIndexed.CanonicalKey;
                    }
                }

                // Slice 16B (Persistent Profile-Owned Watch State): the
                // Watch State CanonicalKey is always exactly what
                // BuildStremioId() produces for this same request - the
                // same "tt1234567" / "tt1234567:2:5" / "kitsu:12345:5"
                // shape already used to query Stremio addons above, never
                // a second, independently-computed identity (Slice 16B
                // spec, Section 4/5). Stashed now, before AggregateAsync,
                // so it is available once playback actually starts
                // further down - null here (BuildStremioId's own "cannot
                // be resolved" case) simply means Watch State is
                // unavailable for this item, same as everywhere else this
                // codebase treats that null.
                string canonicalKey = request.BuildStremioId();
                ContentType contentType = request.Type;

                // Slice 19A: a movie opened from a source that carries no
                // synopsis (e.g. Search's Cinemeta catalog results) has its
                // overview fetched once, in parallel with source discovery,
                // so the Source Selector can show it. Null (no fetch) when
                // an overview is already present or this is not a plain movie.
                Task<string> movieOverviewTask =
                    BeginMovieOverviewHydration(mediaItem, resumePlan, transition);

                // Local Files: the library's playable files for this exact
                // identity, resolved from the persisted index (no network).
                // Empty when Local Files is off / has no match - then nothing
                // below differs from the streaming-only flow.
                bool localIntent =
                    !string.IsNullOrEmpty(mediaItem.LocalKey) ||
                    LocalLibraryManager.IsLocalItemKey(canonicalKey);

                LocalSourceLookup localLookup = await FindLocalSourcesAsync(canonicalKey, mediaItem);

                NormalizedPlaybackSource preselectedLocal =
                    ChoosePreselectedLocalSource(localLookup, mediaItem, canonicalKey, resumePlan, transition);

                if (localIntent && LocalLibraryManager.IsLocalItemKey(canonicalKey) && localLookup.Sources.Count == 0)
                {
                    // An unmatched local file has no streaming equivalent to
                    // fall back to.
                    TeardownEpisodeBrowserOverlay(pendingEpisodeBrowser);
                    ShowPlaybackNotice(DescribeLocalUnavailable(localLookup));
                    return;
                }

                if (localIntent && localLookup.Sources.Count == 0)
                {
                    // A matched file that cannot be played right now: say so
                    // and carry on with the normal streaming flow below.
                    ShowPlaybackNotice(DescribeLocalUnavailable(localLookup));
                }

                PlaybackSourceSet set;

                if (preselectedLocal != null || LocalLibraryManager.IsLocalItemKey(canonicalKey))
                {
                    // Playing a local file directly needs no stream discovery
                    // (and must work offline).
                    set = new PlaybackSourceSet();
                }
                else
                {
                    try
                    {
                        set = await aggregator.AggregateAsync(
                            request);
                    }
                    catch (Exception aggregateEx) when (localLookup.Sources.Count > 0)
                    {
                        // Streaming discovery failed, but the user still has
                        // local copies to choose from.
                        StremioVideoCenterLogger.Info(
                            "Stream discovery failed; offering local sources only. " +
                            UserFacingErrors.ForLog(aggregateEx));

                        set = new PlaybackSourceSet();
                    }
                }

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
                // separately via a non-blocking notice (Slice 20 Stage 2),
                // so a real error is never mistaken for a legitimate
                // zero-result response.
                List<NormalizedPlaybackSource> normalizedSources =
                    normalizer.NormalizeAll(
                        set.Sources);

                // Local copies are offered first. They are a distinct source
                // kind (PlaybackSourceKind.Local), so saved-source matching
                // and "similar source" hints can never confuse them with a
                // streaming source.
                if (localLookup.Sources.Count > 0)
                {
                    normalizedSources.InsertRange(0, localLookup.Sources);
                }

                if (transition != null && transition.Generation != _playbackGeneration)
                {
                    // The player was closed (or replaced) while this episode's
                    // streams were resolving: nothing left to start.
                    return;
                }

                // The Source Selector is now actually ready to show -
                // this is the deliberate hand-off point: the episode
                // browser overlay (if any) is replaced by the Source
                // Selector here, rather than having disappeared back when
                // the episode was first picked.
                TeardownEpisodeBrowserOverlay(
                    pendingEpisodeBrowser);

                // Slice 18B: resume tries the last successfully played source
                // first, matched by stable identity against the sources
                // available NOW (never an old URL). No match -> the normal
                // Source Selector.
                NormalizedPlaybackSource selected =
                    preselectedLocal ?? ResumePlanner.FindSavedSource(resumePlan, normalizedSources);

                // Slice 18D: an episode with no saved source of its own may reuse
                // the source KIND the user was just watching (same addon, same
                // stream label), matched against THIS episode's fresh sources.
                if (selected == null && transition != null && transition.PreferredSource != null)
                {
                    selected = transition.PreferredSource.FindSimilarSource(normalizedSources);
                }

                bool autoSelectedSavedSource = selected != null;

                if (selected == null)
                {
                    // The Source Selector lives beneath the player layer, so a
                    // transition that genuinely needs it closes the player first
                    // (flushing the old episode's progress) - the same as source
                    // failure recovery.
                    await ApplyMovieOverviewAsync(mediaItem, movieOverviewTask);

                    // Slice 20 Stage 2: when the list is empty, say why in the
                    // selector's own empty state (fixed wording only).
                    string emptyExplanation = null;

                    if (normalizedSources.Count == 0 && set.Sources.Count == 0)
                    {
                        emptyExplanation = set.Unresolved.Count == 0
                            ? UserFacingErrors.ForNoSources()
                            : UserFacingErrors.ForUnresolvedSources(set.Unresolved);
                    }

                    selected =
                        await ShowSourceSelectorReplacingPlayerAsync(
                            normalizedSources,
                            mediaItem,
                            emptyExplanation);
                }

                if (selected == null)
                {
                    // Back on the Source Selector: the caller decides where
                    // that leads (see NavigationRules.Back).
                    _sourceSelectionCancelled = true;
                    return;
                }

                // One context for the whole attempt. The profile is captured
                // HERE so progress can never leak into a profile selected
                // later. The candidate list rides along (session-scoped, in
                // memory only) so a genuine mpv load error can return to
                // this Source Selector minus ONLY the failed candidate. See
                // StremioPlayer_PlaybackFailed.
                // A resume plan's profile is authoritative: it is the profile
                // whose Watch State was read.
                Guid? captureProfileId = _profileService?.GetActiveProfileId();
                Guid contextProfileId = resumePlan != null
                    ? resumePlan.ProfileId
                    : (transition != null
                        ? transition.ProfileId
                        : (captureProfileId ?? Guid.Empty));

                PlaybackContext context = new PlaybackContext(
                    contextProfileId,
                    canonicalKey,
                    contentType,
                    mediaItem,
                    selected,
                    resumePlan != null ? resumePlan.StartPositionSeconds : null,
                    normalizedSources);

                // Episode neighbours are known immediately when this series'
                // list is already cached (always the case for a transition).
                AttachCachedEpisodeContext(context);

                if (!autoSelectedSavedSource)
                {
                    await StartPlaybackAsync(context);
                }
                else
                {
                    try
                    {
                        await StartPlaybackAsync(context);
                    }
                    catch (Exception startEx) when (!(startEx is PlayerStartupException))
                    {
                        // (A player that never started is not a bad source:
                        // that falls through to the outer handler instead.)
                        // The saved source could not even be handed to mpv:
                        // fall back to the Source Selector once (no retry
                        // loop). The saved source and position are untouched
                        // because nothing was established.
                        StremioVideoCenterLogger.Info(
                            "Saved source failed to start; falling back to Source Selector. " +
                            UserFacingErrors.ForLog(startEx));

                        context.Candidates.Remove(selected);

                        if (selected.Kind == PlaybackSourceKind.Local)
                        {
                            // A local file that cannot be handed to mpv is
                            // left out of this session's local sources, and
                            // streaming sources are offered in its place.
                            if (_localLibrary != null)
                            {
                                _localLibrary.NotePlaybackFailed(selected);
                            }

                            await AddStreamingFallbackAsync(context);
                        }

                        NormalizedPlaybackSource alternative =
                            await ShowSourceSelectorReplacingPlayerAsync(context.Candidates, mediaItem);

                        if (alternative == null)
                        {
                            _sourceSelectionCancelled = true;
                            return;
                        }

                        await StartPlaybackAsync(context.WithSource(alternative));
                    }
                }
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    string.Format(
                        "Playback start failed ex={0}",
                        UserFacingErrors.ForLog(ex)));

                TeardownEpisodeBrowserOverlay(
                    pendingEpisodeBrowser);

                ClosePlayer();

                // Never ex.Message: player/IPC/HTTP exception text can embed
                // command JSON or a playback/addon URL. Non-blocking: the
                // player has been closed and the caller carries on exactly
                // as it did after the old dialog was dismissed.
                ShowPlaybackNotice(
                    "Unable to start " +
                    (string.IsNullOrWhiteSpace(mediaItem.Title) ? "playback" : "\u201C" + mediaItem.Title + "\u201D") +
                    ". " +
                    UserFacingErrors.ForPlayback(ex));
            }
        }

        // The streaming-addon source pipeline (registry from the configured
        // addon instances, optional Real-Debrid, aggregator, normalizer).
        // Extracted unchanged from PlaySelectedMediaAsync so the streaming
        // fallback after a local-file failure uses the very same wiring.
        private void CreateStreamingServices(
            out PlaybackSourceAggregationService aggregator,
            out PlaybackSourceNormalizationService normalizer)
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
            // Slice 20: restricted to exactly that case, so a legacy
            // addon the user removed (instance gone, URL line still
            // present in an older save) is never resurrected here.
            List<AddonInstance> registeredInstances = _addonInstances;

            if ((registeredInstances == null || registeredInstances.Count == 0) &&
                !string.IsNullOrWhiteSpace(_stremioAddonUrls))
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

            aggregator =
                new PlaybackSourceAggregationService(
                    discovery,
                    resolver);

            normalizer =
                new PlaybackSourceNormalizationService();
        }

        // =========================================================
        // LOCAL FILES: SOURCE LOOKUP / FALLBACK
        // =========================================================

        // The library's playable local sources for this identity. Never
        // throws: any failure means "no local sources", so a local problem
        // can never stop streaming playback.
        private async Task<LocalSourceLookup> FindLocalSourcesAsync(string canonicalKey, MediaItem mediaItem)
        {
            if (_localLibrary == null || string.IsNullOrWhiteSpace(canonicalKey))
            {
                return new LocalSourceLookup();
            }

            try
            {
                return await _localLibrary.FindSourcesAsync(
                    canonicalKey,
                    mediaItem != null ? mediaItem.LocalKey : null);
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: sources could not be looked up. " + UserFacingErrors.ForLog(ex));
                return new LocalSourceLookup();
            }
        }

        // When playback should go straight to a local file (no selector, no
        // stream discovery): the user picked a local file, the identity is
        // an unmatched file's own id, the saved source of a resume was a
        // local file, or the episode just watched was local. Otherwise null
        // and the normal flow runs with local copies listed first.
        private static NormalizedPlaybackSource ChoosePreselectedLocalSource(
            LocalSourceLookup lookup,
            MediaItem mediaItem,
            string canonicalKey,
            ResumePlan resumePlan,
            EpisodeTransition transition)
        {
            if (lookup == null || lookup.Sources.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(mediaItem.LocalKey) || LocalLibraryManager.IsLocalItemKey(canonicalKey))
            {
                return lookup.Sources[0];
            }

            if (resumePlan != null &&
                resumePlan.SavedSource != null &&
                resumePlan.SavedSource.Kind == PlaybackSourceKind.Local)
            {
                return ResumePlanner.FindSavedSource(resumePlan, lookup.Sources);
            }

            if (transition != null &&
                transition.PreferredSource != null &&
                transition.PreferredSource.Kind == PlaybackSourceKind.Local)
            {
                return lookup.Sources[0];
            }

            return null;
        }

        // Fixed wording only: never a file name or path.
        private string DescribeLocalUnavailable(LocalSourceLookup lookup)
        {
            if (_localLibrary == null || !_localLibrary.IsEnabled)
            {
                return "Local Files is turned off, so this local copy can\u2019t be played.";
            }

            return lookup != null && lookup.UnavailableCount > 0
                ? "This local file is no longer available. Check that its drive or folder is connected."
                : "This local file is not in your library any more. Rescan Local Files to refresh it.";
        }

        // After a local file failed: if the candidate list holds no
        // streaming source, discover them now (the normal streaming
        // pipeline) so the Source Selector still offers real alternatives.
        // Nothing to add for an unmatched file (it has no streaming
        // identity). Never throws.
        private async Task AddStreamingFallbackAsync(PlaybackContext context)
        {
            if (context == null ||
                context.Candidates == null ||
                string.IsNullOrWhiteSpace(context.CanonicalKey) ||
                LocalLibraryManager.IsLocalItemKey(context.CanonicalKey) ||
                context.Candidates.Any(c => c.Kind != PlaybackSourceKind.Local))
            {
                return;
            }

            try
            {
                PlaybackSourceAggregationService aggregator;
                PlaybackSourceNormalizationService normalizer;
                CreateStreamingServices(out aggregator, out normalizer);

                var request = new ContentRequest
                {
                    Type = context.ContentType,
                    Title = context.MediaItem != null ? context.MediaItem.Title : null,
                    ExplicitStremioId = context.CanonicalKey
                };

                PlaybackSourceSet set = await aggregator.AggregateAsync(request);

                context.Candidates.AddRange(normalizer.NormalizeAll(set.Sources));
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Stream discovery after a local failure did not complete. " + UserFacingErrors.ForLog(ex));
            }
        }

        // Creates the player if needed and starts playback of the given
        // (already user-selected) source. Shared by the normal selection
        // path and the post-failure re-selection path.
        private async Task StartPlaybackAsync(
            PlaybackContext context)
        {
            // A previous attempt's error notice must not linger over (or
            // under) a new attempt. (The resume-loading toast belongs to this
            // attempt and stays until file-loaded or a terminal path.)
            if (!_resumeLoadingToastActive)
            {
                FeedbackToast?.Hide();
            }

            // Reusing the running player for another episode: persist the
            // episode being left (its recorder is bound to ITS context), then
            // drop that recorder so nothing from it can reach the new episode.
            if (_stremioPlayer != null)
            {
                FlushPlaybackProgress();
            }

            _playbackGeneration++;
            _playbackContext = context;
            _progressRecorder = null;

            if (_stremioPlayer == null)
            {
                // Local Files page underneath: hand focus and input to the
                // player (no-op when that page is not open).
                if (_activeLocalMediaView != null)
                {
                    _activeLocalMediaView.SetPlayerActive(true);
                }

                // Same hand-off for a Movies/TV/Anime/Search/History/My List
                // page left open underneath (undone in ClosePlayer).
                DisablePageUnderPlayer();

                _stremioPlayer =
                    new MpvPlayer();

                _stremioPlayer.CloseRequested +=
                    StremioPlayer_CloseRequested;

                // Slice 20 Stage 3: the subtitle selector asks this view for
                // the external subtitle search when (and only when) it opens.
                _stremioPlayer.ExternalSubtitleProvider =
                    DiscoverExternalSubtitlesAsync;

                // Slice 16B: same subscribe-on-create/unsubscribe-in-
                // ClosePlayer lifecycle as CloseRequested above - see
                // ClosePlayer and StremioPlayer_PlaybackStateChanged.
                _stremioPlayer.PlaybackStateChanged +=
                    StremioPlayer_PlaybackStateChanged;

                // Genuine mpv load/playback failure (end-file reason=error
                // with a file_error) - see StremioPlayer_PlaybackFailed.
                _stremioPlayer.PlaybackFailed +=
                    StremioPlayer_PlaybackFailed;

                // Slice 18A: mpv confirmed the file loaded - the only
                // point at which Watch State/History are created.
                _stremioPlayer.PlaybackEstablished +=
                    StremioPlayer_PlaybackEstablished;

                // Slice 18D: episode navigation and genuine end of file.
                _stremioPlayer.PreviousEpisodeRequested +=
                    StremioPlayer_PreviousEpisodeRequested;
                _stremioPlayer.NextEpisodeRequested +=
                    StremioPlayer_NextEpisodeRequested;
                _stremioPlayer.EndOfFileReached +=
                    StremioPlayer_EndOfFileReached;

                // The player's controls overlay is its own PresentationSource, so
                // its arrow keys never pass through this view's PreviewKeyDown.
                // Forward them to the same handler (controller-echo suppression
                // and keyboard -> D-pad routing) so one input = one action.
                _stremioPlayer.OverlayKeyPreview +=
                    StremioVideoCenterView_PreviewKeyDown;

                PlaybackTestHost.Children.Add(
                    _stremioPlayer);

                PlaybackTestHost.Visibility =
                    Visibility.Visible;
            }

            // Show (or hide, for a movie) the episode line and Previous/Next;
            // if the neighbours are not known yet, load them in the background.
            _stremioPlayer.SetEpisodeNavigation(context.Episode);

            if (context.Episode == null)
            {
                var ignoredNavigation = AttachEpisodeNavigationAsync(context);
            }

            await _stremioPlayer.PlayVideoAsync(context);

            // Slice 18A: Watch State/History are deliberately NOT created
            // here. loadfile succeeding only means mpv accepted the
            // command; the stream may still fail to open. They are
            // established by StremioPlayer_PlaybackEstablished once mpv
            // reports file-loaded.
        }

        private void StremioPlayer_PlaybackEstablished(
            object sender,
            EventArgs e)
        {
            PlaybackContext context = _playbackContext;

            if (sender != _stremioPlayer || context == null || _progressRecorder != null)
            {
                return;
            }

            // Stale-event guard: this notification is delivered asynchronously.
            // It only counts if the player is STILL playing this very context
            // and mpv still reports the file as established - otherwise it
            // belongs to a file that has since been replaced (e.g. a rapid
            // second episode change), and must not create Watch State/History
            // or a recorder for the newer, not-yet-loaded attempt.
            PlaybackState liveState = _stremioPlayer.CurrentPlaybackState;

            if (!ReferenceEquals(_stremioPlayer.CurrentContext, context) ||
                liveState == null ||
                !liveState.IsPlaybackEstablished)
            {
                return;
            }

            // mpv confirmed file-loaded for the current attempt.
            DismissResumeLoadingToast();

            EstablishWatchStateOnPlaybackStart(context);
            EstablishHistoryOnPlaybackStart(context);

            if (_watchStateService != null && context.HasIdentity)
            {
                _progressRecorder = new PlaybackProgressRecorder(_watchStateService, context);
            }
        }

        // =========================================================
        // SOURCE FAILURE RECOVERY
        // =========================================================
        //
        // Only reached for a genuine mpv failure: end-file reason=error
        // WITH a file_error (see PlayerSession.HandleEndFile). Never for
        // eof/stop/quit, and never based on URL text, addon name, label,
        // size or duration. The failed candidate is removed from this
        // selector session only - nothing is persisted - and the user
        // must still choose the next source themselves (no autoplay).
        // Logs use only the addon display name and the candidate's index;
        // never a URL.

        private bool _isRecoveringFromFailure;

        private async void StremioPlayer_PlaybackFailed(
            object sender,
            EventArgs e)
        {
            PlaybackContext failedContext = _playbackContext;

            if (_isRecoveringFromFailure ||
                _isSwappingPlayerForSelector ||
                sender != _stremioPlayer ||
                failedContext == null ||
                !ReferenceEquals(_stremioPlayer.CurrentContext, failedContext))
            {
                // (A failure signal for a file that has already been replaced
                // by another episode/source is not this attempt's failure.)
                return;
            }

            _isRecoveringFromFailure = true;

            try
            {
                NormalizedPlaybackSource failed = failedContext.Source;
                MediaItem mediaItem = failedContext.MediaItem;
                List<NormalizedPlaybackSource> remaining = failedContext.Candidates;

                int failedIndex = remaining.IndexOf(failed);
                remaining.Remove(failed);

                if (failed.Kind == PlaybackSourceKind.Local)
                {
                    // mpv could not play this local file: leave it out of the
                    // session's local sources and make sure the streaming
                    // sources are still on offer.
                    if (_localLibrary != null)
                    {
                        _localLibrary.NotePlaybackFailed(failed);
                    }

                    await AddStreamingFallbackAsync(failedContext);
                }

                StremioVideoCenterLogger.Info(
                    string.Format(
                        "Source failed to play (mpv error); removed from this selection. addon={0} index={1} remaining={2}",
                        !string.IsNullOrWhiteSpace(failed.SourceAddonName)
                            ? failed.SourceAddonName
                            : "(unnamed addon)",
                        failedIndex,
                        remaining.Count));

                // ClosePlayer flushes any progress already recorded for this
                // attempt (a no-op if it never established) and clears the
                // active context; failedContext keeps the identity for the
                // next source.
                // An empty list shows the selector's existing "No playable
                // sources found." state. The player is closed under the
                // selector (no Home frame), flushing any recorded progress.
                // Slice 20 Stage 2: the selector now says the previous source
                // could not be played (it used to reappear with no explanation).
                NormalizedPlaybackSource next =
                    await ShowSourceSelectorReplacingPlayerAsync(
                        remaining,
                        mediaItem,
                        UserFacingErrors.ForPlaybackFailedAfterStart());

                if (next == null)
                {
                    // (Re-armed: a player started from the Episode Selector must
                    // be able to report its own failure.)
                    _isRecoveringFromFailure = false;
                    await ContinueAfterSourceSelectorBackAsync(failedContext);
                    return;
                }

                // Re-arm before starting: the next source's own error may
                // arrive while PlayVideoAsync is still awaiting.
                _isRecoveringFromFailure = false;

                await StartPlaybackAsync(failedContext.WithSource(next));
            }
            catch (Exception ex)
            {
                // Message deliberately omitted from the UI: exception text
                // from the player layer can embed the playback URL.
                StremioVideoCenterLogger.Info(
                    "Source recovery failed: " + UserFacingErrors.ForLog(ex));

                ClosePlayer();
                ShowPlaybackNotice("Unable to start playback. " + UserFacingErrors.ForPlayback(ex));
            }
            finally
            {
                _isRecoveringFromFailure = false;
            }
        }

        // =========================================================
        // "SEE MORE" HANDLERS (Slice 15: real navigation)
        // =========================================================

        private async void MoreMovies_Clicked(object sender, EventArgs e)
        {
            // Opened from a Home row, not a navigation button: the usual
            // "return to Home" focus applies when the page closes.
            _catalogueReturnFocusButton = null;
            await ShowMoviesPageAsync();
        }

        private async void MoreTvShows_Clicked(object sender, EventArgs e)
        {
            _catalogueReturnFocusButton = null;
            await ShowTvPageAsync();
        }

        private async void MoreAnime_Clicked(object sender, EventArgs e)
        {
            _catalogueReturnFocusButton = null;
            await ShowAnimePageAsync();
        }

        // =========================================================
        // TOP NAVIGATION BUTTON HANDLERS (Slice 15)
        // =========================================================

        private async void MoviesButton_Click(object sender, RoutedEventArgs e)
        {
            _catalogueReturnFocusButton = MoviesButton;
            await ShowMoviesPageAsync();
        }

        private async void TvShowsButton_Click(object sender, RoutedEventArgs e)
        {
            _catalogueReturnFocusButton = TvShowsButton;
            await ShowTvPageAsync();
        }

        private async void AnimeButton_Click(object sender, RoutedEventArgs e)
        {
            _catalogueReturnFocusButton = AnimeButton;
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
            EventHandler<string> onNavigationRequested = null;

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

            // Persistent hero navigation task: records the requested
            // destination before Closed fires (the page raises this event
            // and then immediately calls RaiseClosed() itself, both
            // synchronously - see CatalogueBrowseView.RaiseNavigationRequested)
            // so it's already set by the time this method resumes below.
            onNavigationRequested = (sender, destination) =>
            {
                _pendingPageNavigationDestination = destination;
            };

            page.Closed += onClosed;
            page.MediaSelected += onMediaSelected;
            page.NavigationRequested += onNavigationRequested;

            _activeMoviesPage = isTv ? null : page;
            _activeTvPage = isTv ? page : null;

            System.Windows.Controls.Grid host = isTv ? TvPageHost : MoviesPageHost;

            host.Children.Add(page);
            host.Visibility = Visibility.Visible;

            var cinemetaService = new CinemetaService();
            var kitsuService = new KitsuService();
            var catalogueService = new CatalogueService(cinemetaService, kitsuService);

            page.Initialize(isTv, catalogueService);

            await completionSource.Task;

            page.Closed -= onClosed;
            page.MediaSelected -= onMediaSelected;
            page.NavigationRequested -= onNavigationRequested;

            host.Children.Remove(page);
            host.Visibility = Visibility.Collapsed;

            _activeMoviesPage = null;
            _activeTvPage = null;

            await NavigateToPendingDestinationAsync();
        }

        // Persistent hero navigation task: called after each Movies/TV/
        // Anime page has fully torn itself down (host cleared, _active*
        // Page nulled) - opens whichever destination a quick-nav button
        // requested, if any, otherwise falls back to the existing
        // "return to Home" behaviour (FocusFirstAvailableSection) exactly
        // as before this task.
        private async Task NavigateToPendingDestinationAsync()
        {
            string destination = _pendingPageNavigationDestination;
            _pendingPageNavigationDestination = null;

            switch (destination)
            {
                case "Movies":
                    _catalogueReturnFocusButton = MoviesButton;
                    await ShowMoviesPageAsync();
                    return;

                case "TvShows":
                    _catalogueReturnFocusButton = TvShowsButton;
                    await ShowTvPageAsync();
                    return;

                case "Anime":
                    _catalogueReturnFocusButton = AnimeButton;
                    await ShowAnimePageAsync();
                    return;

                default:
                    // The chain of pages has ended: return focus to the
                    // navigation button the last page was opened from (null
                    // when it was opened from a Home row).
                    Button returnButton = _catalogueReturnFocusButton;
                    _catalogueReturnFocusButton = null;

                    if (returnButton != null)
                    {
                        RestoreFocusToNavigationButton(returnButton);
                    }
                    else
                    {
                        // Opened from a Home row ("See more"): the original
                        // return-to-Home behaviour.
                        FocusFirstAvailableSection();
                    }

                    return;
            }
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
            EventHandler<string> onNavigationRequested = null;

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

            // Persistent hero navigation task: see the identical remark
            // on ShowCatalogueBrowsePageAsync's own onNavigationRequested.
            onNavigationRequested = (sender, destination) =>
            {
                _pendingPageNavigationDestination = destination;
            };

            page.Closed += onClosed;
            page.MediaSelected += onMediaSelected;
            page.NavigationRequested += onNavigationRequested;

            _activeAnimePage = page;

            AnimePageHost.Children.Add(page);
            AnimePageHost.Visibility = Visibility.Visible;

            // This page does not need a CatalogueService at
            // all - Kitsu (via KitsuService.BrowseAnimeAsync) is now
            // the sole source for the Anime Browse filter/discovery
            // section.
            var kitsuService = new KitsuService();

            page.Initialize(kitsuService);

            await completionSource.Task;

            page.Closed -= onClosed;
            page.MediaSelected -= onMediaSelected;
            page.NavigationRequested -= onNavigationRequested;

            AnimePageHost.Children.Remove(page);
            AnimePageHost.Visibility = Visibility.Collapsed;

            _activeAnimePage = null;

            await NavigateToPendingDestinationAsync();
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

            RestoreFocusToNavigationButton(SearchButton);
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

        // Slice 19B: the Source Selector provider filter last chosen, scoped
        // to the content (title + season + episode) it was chosen for.
        // _providerFilterKey null = All Sources. In-memory only.
        private string _providerFilterContentKey;
        private string _providerFilterKey;

        private static string BuildProviderFilterContentKey(MediaItem mediaItem)
        {
            if (mediaItem == null || string.IsNullOrWhiteSpace(mediaItem.StremioId))
            {
                return null;
            }

            return mediaItem.StremioId + "|" +
                (mediaItem.Season.HasValue ? mediaItem.Season.Value.ToString() : string.Empty) + "|" +
                (mediaItem.Episode.HasValue ? mediaItem.Episode.Value.ToString() : string.Empty);
        }

        private async Task<NormalizedPlaybackSource> ShowSourceSelectorAsync(
            List<NormalizedPlaybackSource> sources,
            MediaItem mediaItem,
            string failureNotice = null)
        {

            var selector = new SourceSelectorView();

            var completionSource =
                new TaskCompletionSource<NormalizedPlaybackSource>();

            EventHandler<SourceSelectedEventArgs> onSelected = null;
            EventHandler onCancelled = null;
            EventHandler onMyListToggleRequested = null;

            onSelected = (sender, args) =>
            {
                completionSource.TrySetResult(args.Source);
            };

            onCancelled = (sender, args) =>
            {
                completionSource.TrySetResult(null);
            };

            // Slice 17B: the selector never talks to MyListService itself -
            // it only tells us its My List button was pressed, and we tell
            // it back what to show (see HandleSourceSelectorMyListToggle).
            onMyListToggleRequested = (sender, args) =>
            {
                HandleSourceSelectorMyListToggle(selector, mediaItem);
            };

            selector.SourceSelected += onSelected;
            selector.SelectionCancelled += onCancelled;
            selector.MyListToggleRequested += onMyListToggleRequested;

            _activeSourceSelector = selector;

            SourceSelectorHost.Children.Add(selector);
            SourceSelectorHost.Visibility = Visibility.Visible;

            // Slice 19B: restore the provider filter the user last chose for
            // THIS content (e.g. on return from playback). Ignored by the
            // selector if that provider has no sources in this list.
            string providerFilterContentKey = BuildProviderFilterContentKey(mediaItem);

            string preferredProviderKey =
                providerFilterContentKey != null &&
                string.Equals(providerFilterContentKey, _providerFilterContentKey, StringComparison.Ordinal)
                    ? _providerFilterKey
                    : null;

            selector.Populate(sources, mediaItem, preferredProviderKey);
            selector.SetMyListState(IsMediaItemInMyList(mediaItem));

            // The selector is up: the resume-loading toast (if any) is done.
            DismissResumeLoadingToast();

            // Slice 20 Stage 2: optional explanation of why the user is
            // looking at this selector. With nothing to list it becomes part
            // of the empty state; otherwise it is a transient notice.
            bool failureNoticeToasted = false;

            if (!string.IsNullOrWhiteSpace(failureNotice))
            {
                if (sources == null || sources.Count == 0)
                {
                    selector.SetEmptyStateDetail(failureNotice);
                }
                else
                {
                    ShowPlaybackNotice(failureNotice);
                    failureNoticeToasted = true;
                }
            }

            // Diagnostic only: no provider key/URL is logged.
            StremioVideoCenterLogger.Info(
                string.Format(
                    "[ProviderFilter] contentKeyAvailable={0} restoreRequested={1} restored={2}",
                    providerFilterContentKey != null,
                    preferredProviderKey != null,
                    selector.SelectedProviderKey != null));

            // This is the point the user's diagnostic request calls out
            // specifically: the Source Selector control has now been
            // constructed, added to the visual tree, made Visible, and
            // populated with data - i.e. actually displayed - and
            // control is about to yield on completionSource.Task waiting
            // for the user to pick a source (or cancel).

            NormalizedPlaybackSource result =
                await completionSource.Task;

            // Leaving the selector (a source chosen, or Back) ends the
            // notice that explained it.
            if (failureNoticeToasted)
            {
                FeedbackToast?.Hide();
            }

            // Slice 19B: remember the provider filter for this content. The
            // key is kept in memory only (it derives from a configured
            // manifest URL) - never logged, displayed or persisted.
            _providerFilterContentKey = providerFilterContentKey;
            _providerFilterKey = selector.SelectedProviderKey;

            selector.SourceSelected -= onSelected;
            selector.SelectionCancelled -= onCancelled;
            selector.MyListToggleRequested -= onMyListToggleRequested;

            SourceSelectorHost.Children.Remove(selector);
            SourceSelectorHost.Visibility = Visibility.Collapsed;

            _activeSourceSelector = null;

            FocusFirstAvailableSection();

            return result;
        }

        // =========================================================
        // SOURCE SELECTOR MY LIST INTEGRATION (Slice 17B)
        // =========================================================
        //
        // The only place this view wires SourceSelectorView's My List
        // button to MyListService - the control itself never calls the
        // service (see its own remarks on staying self-contained). Movies
        // add the movie; an episode-shaped MediaItem (Season/Episode both
        // populated, as EpisodeBrowserView.BuildEpisodeMediaItem produces)
        // adds the series, because MediaItem.StremioId is always the
        // show/movie-level id (see MediaItem.Season/.Episode remarks) -
        // MyListService.Add/Toggle already use it verbatim, so no
        // special-casing by content type is needed here.

        private bool IsMediaItemInMyList(MediaItem mediaItem)
        {
            if (_myListService == null ||
                mediaItem == null ||
                string.IsNullOrWhiteSpace(mediaItem.StremioId))
            {
                return false;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (!activeProfileId.HasValue)
            {
                return false;
            }

            return _myListService.IsInMyList(activeProfileId.Value, mediaItem.StremioId);
        }

        private void HandleSourceSelectorMyListToggle(
            SourceSelectorView selector,
            MediaItem mediaItem)
        {
            bool? nowInList = ToggleMyListAndRefresh(mediaItem);

            if (nowInList.HasValue)
            {
                selector.SetMyListState(nowInList.Value);
            }
        }

        // Shared by the Source Selector (movies) and the Episode Browser
        // (TV/Anime, series-level `show`). Returns null when nothing was
        // toggled (no service/item/active profile).
        private bool? ToggleMyListAndRefresh(MediaItem mediaItem)
        {
            if (_myListService == null || mediaItem == null)
            {
                return null;
            }

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (!activeProfileId.HasValue)
            {
                return null;
            }

            bool nowInList = _myListService.Toggle(activeProfileId.Value, mediaItem);

            // Slice 17B: a toggle changes the same underlying MyListService
            // state the Home row and Hero button display - both must
            // reflect it immediately, not only once the user happens to
            // revisit Home or move the Hero carousel.
            RefreshMyList();
            RefreshHeroMyListButtonState();
            RefreshRecommendations(clearImmediately: false);

            ShowToast(nowInList ? "Added to My List" : "Removed from My List");

            return nowInList;
        }

        // =========================================================
        // HERO WATCH NOW / MY LIST INTEGRATION (Slice 17B)
        // =========================================================
        //
        // HeroItem carries its own canonical StremioId/Type/Genres
        // directly (see that class's "SLICE 17B" remarks), so both
        // handlers below build a MediaItem from the CURRENTLY DISPLAYED
        // Hero item (vm.CurrentHeroItem) and route through the exact same
        // pipelines every other card on this page already uses - no
        // separate Hero playback or My List architecture.

        // Watch Now must NOT autoplay - it routes into the same
        // HandleMediaSelectedAsync pipeline a catalogue card's selection
        // uses: a Movie plays directly through the existing flow, a TV/
        // Anime item opens the Episode Browser first. Nothing here
        // bypasses Source Selector.
        private async void HeroWatchNowButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            HomeViewModel vm = DataContext as HomeViewModel;

            MediaItem heroMediaItem = BuildMediaItemFromHeroItem(vm?.CurrentHeroItem);

            if (heroMediaItem == null)
            {
                return;
            }

            await HandleMediaSelectedAsync(heroMediaItem);
        }

        // Toggles the currently displayed Hero item's My List membership
        // via MyListService (same source of truth as Source Selector's
        // own toggle above), then refreshes the Hero button itself plus
        // the Home row so nothing is left stale.
        private void HeroMyListButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            HomeViewModel vm = DataContext as HomeViewModel;

            MediaItem heroMediaItem = BuildMediaItemFromHeroItem(vm?.CurrentHeroItem);

            Guid? activeProfileId = _profileService?.GetActiveProfileId();

            if (_myListService == null || heroMediaItem == null || !activeProfileId.HasValue)
            {
                return;
            }

            bool nowInList = _myListService.Toggle(activeProfileId.Value, heroMediaItem);

            RefreshMyList();
            RefreshHeroMyListButtonState();
            RefreshRecommendations(clearImmediately: false);

            ShowToast(nowInList ? "Added to My List" : "Removed from My List");
        }

        // Reflects whether the currently displayed Hero item is already
        // in My List - called whenever the Hero carousel moves
        // (ChangeHero/SetHeroIndex), whenever the active profile changes
        // (OnActiveProfileChanged), after initial Hero load, and after
        // any My List mutation that could affect this item (the Hero
        // toggle itself and Source Selector's toggle, above). Always
        // re-reads MyListService directly - never a cached flag - so it
        // can never go stale.
        private void RefreshHeroMyListButtonState()
        {
            if (HeroMyListButton == null)
            {
                return;
            }

            HomeViewModel vm = DataContext as HomeViewModel;

            MediaItem heroMediaItem = BuildMediaItemFromHeroItem(vm?.CurrentHeroItem);

            bool isInList = IsMediaItemInMyList(heroMediaItem);

            HeroMyListButton.Content = isInList
                ? "✓  IN MY LIST"
                : "＋  MY LIST";

            HeroMyListButton.ToolTip = isInList
                ? "Remove from My List"
                : "Add to My List";
        }

        // Builds a MediaItem from the given HeroItem - the same shape
        // every other card's MediaItem already takes (StremioId/Title/
        // PosterPath/Year/Type/Genres), so it can be handed to
        // HandleMediaSelectedAsync, MyListService, or GetContentType
        // without any Hero-specific special-casing in those methods.
        // Returns null for a HeroItem with no resolvable canonical
        // identity, mirroring MyListService.Add/Toggle's own
        // StremioId-required guard.
        private MediaItem BuildMediaItemFromHeroItem(HeroItem heroItem)
        {
            if (heroItem == null || string.IsNullOrWhiteSpace(heroItem.StremioId))
            {
                return null;
            }

            return new MediaItem
            {
                StremioId = heroItem.StremioId,
                Title = heroItem.Title,
                Overview = heroItem.Synopsis,
                PosterPath = heroItem.PosterUrl,
                Year = heroItem.Year,
                Type = heroItem.Type,
                Genres = heroItem.Genres != null
                    ? new List<string>(heroItem.Genres)
                    : new List<string>()
            };
        }

        // =========================================================
        // WATCH STATE INTEGRATION (Slice 16B)
        // =========================================================
        //
        // These three methods are the only place this view talks to
        // WatchStateService. All three are no-ops (never throw, never
        // block playback) if WatchStateService, the active profile, or
        // this item's canonical identity aren't available - Watch State
        // is additive to playback, never a precondition for it.

        private void EstablishWatchStateOnPlaybackStart(PlaybackContext context)
        {
            if (_watchStateService == null || context == null || !context.HasIdentity)
            {
                return;
            }

            MediaItem mediaItem = context.MediaItem;

            // Slice 15/16B: a Kitsu-sourced episode's "show" identity is
            // its own StremioId ("kitsu:<id>"); an ordinary TV/anime
            // episode's is mediaItem.StremioId ("tt..."); a movie or
            // show-level item has no separate show identity - see
            // WatchStateEntry.ShowStremioId remarks.
            string showStremioId =
                mediaItem != null &&
                mediaItem.Season.HasValue &&
                mediaItem.Episode.HasValue
                    ? mediaItem.StremioId
                    : null;

            // Kitsu's absolute-episode identity must never be presented
            // as a season/episode pair (Slice 16B spec, Section 15) -
            // SeasonNumber is left null for a Kitsu-sourced entry, same
            // as WatchStateEntry.SeasonNumber's own remarks.
            bool isKitsuSourced =
                mediaItem != null &&
                !string.IsNullOrWhiteSpace(mediaItem.StremioId) &&
                mediaItem.StremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);

            _watchStateService.UpsertOnPlaybackStart(
                context.ProfileId,
                context.ContentType,
                context.CanonicalKey,
                showStremioId,
                isKitsuSourced ? (int?)null : mediaItem?.Season,
                mediaItem?.Episode,
                mediaItem?.Title,
                mediaItem?.PosterPath,
                mediaItem?.Year,
                mediaItem?.Type,
                context.BuildSavedSource());
        }

        // Slice 16C: same "playback actually started" moment as
        // EstablishWatchStateOnPlaybackStart above, but updates/creates
        // the series/movie-level History entry instead. seriesCanonicalKey
        // is always mediaItem.StremioId - unlike Watch State's CanonicalKey,
        // this is never episode-qualified (EpisodeBrowserView.
        // BuildEpisodeMediaItem already builds every episode MediaItem's
        // StremioId as the show-level id alone), so it needs no
        // isKitsuSourced-style branching to resolve the series identity
        // itself - only the display season/episode numbers still need
        // that guard, mirroring EstablishWatchStateOnPlaybackStart exactly.
        private void EstablishHistoryOnPlaybackStart(PlaybackContext context)
        {
            if (_watchHistoryService == null || context == null || !context.HasIdentity)
            {
                return;
            }

            MediaItem mediaItem = context.MediaItem;

            if (mediaItem == null || string.IsNullOrWhiteSpace(mediaItem.StremioId))
            {
                return;
            }

            // An unmatched local file has no catalogue title to open from
            // History (its id is a path-free local id, not a title id), so it
            // is tracked by Watch State / Continue Watching only.
            if (LocalLibraryManager.IsLocalItemKey(mediaItem.StremioId) ||
                LocalLibraryManager.IsLocalShowKey(mediaItem.StremioId))
            {
                return;
            }

            bool isKitsuSourced =
                mediaItem.StremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);

            _watchHistoryService.RecordVisit(
                context.ProfileId,
                context.ContentType,
                mediaItem.StremioId,
                context.CanonicalKey,
                isKitsuSourced ? (int?)null : mediaItem.Season,
                mediaItem.Episode,
                mediaItem.Title,
                mediaItem.PosterPath,
                mediaItem.Year,
                mediaItem.Type,
                mediaItem.Genres);
        }

        private void StremioPlayer_PlaybackStateChanged(
            object sender,
            EventArgs e)
        {
            // In-memory update on every tick; the recorder throttles disk
            // writes and persists immediately on pause/seek/completion.
            if (sender == _stremioPlayer && _progressRecorder != null && _stremioPlayer != null)
            {
                _progressRecorder.Update(_stremioPlayer.CurrentPlaybackState);
            }
        }

        // Immediate persist of the last VALID position. Called from
        // ClosePlayer (covers user close and failure recovery). A no-op
        // when the attempt never established, so an aborted start can never
        // overwrite a saved position.
        private void FlushPlaybackProgress()
        {
            if (_progressRecorder != null && _stremioPlayer != null)
            {
                _progressRecorder.Flush(_stremioPlayer.CurrentPlaybackState);
            }
        }

        // =========================================================
        // PLAYER CLOSE
        // =========================================================

        // Back from the player (Exit button, B, Esc, Backspace - all of which
        // arrive here as one CloseRequested): a deliberate exit, NOT an end of
        // file, so nothing here can reach AutoNextPolicy. The player is closed
        // (flushing valid progress) and the Source Selector for the exact media
        // that was playing is shown again.
        private async void StremioPlayer_CloseRequested(
            object sender,
            EventArgs e)
        {
            if (sender != _stremioPlayer || _isReturningToSelector || _isSwappingPlayerForSelector)
            {
                return;
            }

            // Captured BEFORE ClosePlayer drops it: it still holds the exact
            // item/episode, its canonical key and the source candidates.
            PlaybackContext context = _playbackContext;

            _isReturningToSelector = true;

            try
            {
                if (context == null || context.MediaItem == null || context.Candidates == null)
                {
                    // Nothing to go back to: just close.
                    ClosePlayer();
                }
                else
                {
                    // The selector is mounted BEFORE the player is closed (see
                    // ShowSourceSelectorReplacingPlayerAsync), so there is no
                    // intermediate Home screen.
                    await ReturnToSourceSelectorAsync(context);
                }
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Return to Source Selector failed: " + UserFacingErrors.ForLog(ex));

                ClosePlayer();
            }
            finally
            {
                _isReturningToSelector = false;
            }
        }

        private bool _isReturningToSelector;

        // Set by PlaySelectedMediaAsync when the user backs out of the Source
        // Selector without choosing a source.
        private bool _sourceSelectionCancelled;

        // Re-shows the Source Selector for the media of a player that was just
        // closed. Picking a source plays it (resuming from where the user
        // actually stopped, read from the Watch State just flushed); Back goes
        // to the Episode Selector for an episode, or Home for a movie.
        private async Task ReturnToSourceSelectorAsync(PlaybackContext context)
        {
            if (context == null || context.MediaItem == null || context.Candidates == null)
            {
                return;
            }

            // Closes the player (flushing progress) as part of showing the
            // selector, in the right order.
            NormalizedPlaybackSource next =
                await ShowSourceSelectorReplacingPlayerAsync(context.Candidates, context.MediaItem);

            // The new player must be closable while it loads.
            _isReturningToSelector = false;

            if (next != null)
            {
                ResumePlan plan = ResumePlanner.Create(
                    _watchStateService,
                    context.ProfileId,
                    context.CanonicalKey);

                await StartPlaybackAsync(
                    context.WithSource(next, plan != null ? plan.StartPositionSeconds : null));

                return;
            }

            await ContinueAfterSourceSelectorBackAsync(context);
        }

        // Back on the Source Selector: NavigationRules decides the target.
        private async Task ContinueAfterSourceSelectorBackAsync(PlaybackContext context)
        {
            bool isEpisode = context.Episode != null || IsEpisodeShaped(context.MediaItem);

            if (NavigationRules.Back(NavScreen.SourceSelector, isEpisode).Target == NavScreen.EpisodeSelector)
            {
                await BrowseAndPlayEpisodesAsync(BuildSeriesItem(context));
            }
        }

        // The series-level item the Episode Selector needs, rebuilt from the
        // episode that was playing.
        private static MediaItem BuildSeriesItem(PlaybackContext context)
        {
            MediaItem episode = context.MediaItem;

            return new MediaItem
            {
                StremioId = episode.StremioId,
                Title = context.Episode != null
                    ? context.Episode.SeriesTitle
                    : EpisodeContext.DeriveSeriesTitle(episode.Title, null),
                PosterPath = episode.PosterPath,
                Year = episode.Year,
                Type = episode.Type,
                Genres = episode.Genres != null ? new List<string>(episode.Genres) : null
            };
        }

        // Episode Selector -> episode -> Source Selector/player, repeating while
        // the user backs out of a Source Selector (which returns here).
        // Back on the Episode Selector itself ends the loop at Home.
        private async Task BrowseAndPlayEpisodesAsync(MediaItem series)
        {
            while (true)
            {
                MediaItem episode = await ShowEpisodeBrowserAsync(series);

                if (episode == null)
                {
                    return;
                }

                await PlaySelectedMediaAsync(episode);

                if (!_sourceSelectionCancelled)
                {
                    return;
                }
            }
        }

        // restoreHomeFocus: false when a Source Selector is being shown in the
        // player's place - the queued Home refocus would otherwise steal focus
        // from it (and, being the "nothing on top" destination, is exactly what
        // must not be navigated to).
        private void ClosePlayer(bool restoreHomeFocus = true)
        {
            // The player is going away: playback of this attempt will not
            // establish, so the resume-loading toast must not outlive it.
            DismissResumeLoadingToast();

            if (_stremioPlayer != null)
            {
                // Slice 16B: persist whatever position/duration is
                // currently known ONE more time before the player is
                // torn down - "ensure the latest known playback state is
                // persisted before playback is discarded" (Slice 16B
                // spec, Section 9's "Player closes" requirement). Must
                // run before DisposePlayer() below, which nulls out the
                // session CurrentPlaybackState reads from.
                FlushPlaybackProgress();

                // Slice 16C fix: Continue Watching must reflect the
                // just-finished playback session without waiting for the
                // next profile switch or app restart - RefreshContinueWatching()
                // was previously only called from OnActiveProfileChanged
                // (startup/Switch Profile). Runtime diagnostics (Slice 16C
                // data-flow investigation) confirmed WatchState itself was
                // always being created/updated correctly here - the gap was
                // purely this missing refresh call. Reuses the exact same
                // WatchStateService.GetEntriesForProfile() read
                // RefreshContinueWatching already performs elsewhere - no
                // new timer, no new polling, no change to WatchState
                // persistence itself.
                RefreshContinueWatching();

                // Slice 17C: playback may have changed history/completion.
                RefreshRecommendations(clearImmediately: false);

                // Slice 18B: History's progress bars come from Watch State
                // too; if History is open underneath the player, refresh it.
                if (_activeHistoryView != null)
                {
                    _activeHistoryView.Refresh(BuildHistoryDisplayItems());
                }

                try
                {
                    _stremioPlayer.CloseRequested -=
                        StremioPlayer_CloseRequested;
                    _stremioPlayer.PlaybackStateChanged -=
                        StremioPlayer_PlaybackStateChanged;
                    _stremioPlayer.PlaybackFailed -=
                        StremioPlayer_PlaybackFailed;
                    _stremioPlayer.PlaybackEstablished -=
                        StremioPlayer_PlaybackEstablished;
                    _stremioPlayer.PreviousEpisodeRequested -=
                        StremioPlayer_PreviousEpisodeRequested;
                    _stremioPlayer.NextEpisodeRequested -=
                        StremioPlayer_NextEpisodeRequested;
                    _stremioPlayer.EndOfFileReached -=
                        StremioPlayer_EndOfFileReached;
                    _stremioPlayer.OverlayKeyPreview -=
                        StremioVideoCenterView_PreviewKeyDown;

                    // Slice 22 (white-flash test): hide the native video
                    // window before the blocking disposal below.
                    try
                    {
                        _stremioPlayer.HideVideoForClose();
                    }
                    catch (Exception hideEx)
                    {
                        StremioVideoCenterLogger.Warn(
                            "Hiding the video window before close failed: " + UserFacingErrors.ForLog(hideEx));
                    }

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

            // Slice 18A: no player open - drop the attempt's context and
            // recorder (the next playback sets them fresh). The generation
            // bump cancels any episode transition still resolving streams.
            _playbackGeneration++;
            _playbackContext = null;
            _progressRecorder = null;

            PlaybackTestHost.Visibility =
                Visibility.Collapsed;

            // Every player teardown (normal close, startup failure, load
            // failure, selector swap) passes through here, so this is the one
            // place a page disabled for the player is re-enabled - and it runs
            // whether or not a player instance still existed above.
            FrameworkElement pageUnderPlayer = _pageDisabledUnderPlayer;
            IInputElement focusBeforePlayer = _focusBeforePlayerOnPage;
            _pageDisabledUnderPlayer = null;
            _focusBeforePlayerOnPage = null;

            if (pageUnderPlayer != null)
            {
                pageUnderPlayer.IsEnabled = true;
            }

            // Local Files page underneath: it takes focus back, instead of
            // the Home focus restoration below.
            LocalMediaView localPageUnderPlayer = _activeLocalMediaView;

            if (localPageUnderPlayer != null)
            {
                localPageUnderPlayer.SetPlayerActive(false);

                if (restoreHomeFocus)
                {
                    localPageUnderPlayer.FocusAfterPlayback();
                    return;
                }
            }

            // Focus-restoration timing fix: unlike every other overlay
            // teardown that calls FocusFirstAvailableSection() (Episode
            // Browser/Local Media/History/Search/Source Selector), the
            // player is the only overlay backed by a native child window
            // (MpvHost : HwndHost, embedding the real mpv process's
            // window). DisposePlayer() above tears that native window
            // down, which can race with a synchronous WPF Focus() call
            // made in the same dispatcher frame, leaving Home with no
            // focused element after B/Exit. Deferring via the same
            // Dispatcher.BeginInvoke pattern already used for initial
            // Home focus (see StremioVideoCenterView_Loaded) lets that
            // native teardown's focus transition fully settle first -
            // same FocusFirstAvailableSection() target and destination
            // as before, only the timing changes.
            if (restoreHomeFocus)
            {
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        // A page that was open under the player gets its own
                        // focus back when nothing else owns it; otherwise the
                        // usual target (which already honours Movie Details).
                        if (!TryRestoreFocusToPageUnderPlayer(pageUnderPlayer, focusBeforePlayer))
                        {
                            FocusFirstAvailableSection();
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // =========================================================
        // PAGE UNDER THE PLAYER (input / focus hand-off)
        // =========================================================
        //
        // Movies, TV, Anime, Search, History and My List stay open underneath
        // a player started from them. Left enabled they keep (or take back)
        // keyboard focus and, before the router yielded to the player, also
        // consumed the keys/buttons meant for it - so the player could not be
        // controlled and clicking it did not help. LocalMediaView solves the
        // same problem with SetPlayerActive(); this is the equivalent for the
        // other pages. The page is disabled when the player is created and
        // re-enabled in ClosePlayer, the single teardown point.

        // The page disabled for the current player (null when none).
        private FrameworkElement _pageDisabledUnderPlayer;

        // The element that held keyboard focus inside that page when the
        // player was created (null when focus was elsewhere).
        private IInputElement _focusBeforePlayerOnPage;

        private FrameworkElement GetOpenPageUnderPlayer()
        {
            return (FrameworkElement)_activeMoviesPage
                ?? (FrameworkElement)_activeTvPage
                ?? (FrameworkElement)_activeAnimePage
                ?? (FrameworkElement)_activeSearchPage
                ?? (FrameworkElement)_activeHistoryView
                ?? _activeMyListView;
        }

        private void DisablePageUnderPlayer()
        {
            FrameworkElement page = GetOpenPageUnderPlayer();

            if (page == null)
            {
                return;
            }

            // Remember focus only when it is genuinely inside the page.
            IInputElement focused = Keyboard.FocusedElement;
            Visual focusedVisual = focused as Visual;

            _focusBeforePlayerOnPage =
                focusedVisual != null && page.IsAncestorOf(focusedVisual)
                    ? focused
                    : null;

            _pageDisabledUnderPlayer = page;
            page.IsEnabled = false;
        }

        // True when focus was handed back to the element the page had before
        // the player. False (nothing changed) whenever another overlay owns
        // focus, a new player is already up, or the element is gone.
        private bool TryRestoreFocusToPageUnderPlayer(
            FrameworkElement page,
            IInputElement previousFocus)
        {
            if (page == null ||
                previousFocus == null ||
                _stremioPlayer != null ||
                _activeMovieDetails != null ||
                _activeEpisodeBrowser != null ||
                _activeSourceSelector != null)
            {
                return false;
            }

            UIElement previous = previousFocus as UIElement;

            if (previous == null ||
                !previous.IsVisible ||
                !previous.IsEnabled ||
                !previous.Focusable ||
                !page.IsAncestorOf(previous))
            {
                return false;
            }

            Keyboard.Focus(previous);

            return ReferenceEquals(Keyboard.FocusedElement, previous);
        }

        // Shows the Source Selector IN PLACE OF the player with no frame in
        // between: the selector is mounted first (ShowSourceSelectorAsync runs
        // synchronously up to awaiting the user), and only then is the player
        // closed - all within one dispatcher turn, so Home (which sits beneath
        // both) is never rendered. The old player is stopped/disposed and its
        // progress flushed exactly as before; only the order changed.
        private async Task<NormalizedPlaybackSource> ShowSourceSelectorReplacingPlayerAsync(
            List<NormalizedPlaybackSource> sources,
            MediaItem mediaItem,
            string failureNotice = null)
        {
            Task<NormalizedPlaybackSource> selection =
                ShowSourceSelectorAsync(sources, mediaItem, failureNotice);

            MpvPlayer playerToClose = _stremioPlayer;

            if (playerToClose != null)
            {
                // ClosePlayer blocks the UI thread while mpv quits, so the
                // selector mounted above would not be presented until it
                // returned (a black gap). Experiment (option B) order:
                //   1. yield at Render priority while the native video window
                //      is STILL visible, so the selector's (expensive) layout
                //      and render run under the video;
                //   2. hide the native video window and the controls overlay;
                //   3. yield at Render priority again, so the already-laid-out
                //      selector is presented as soon as the window is hidden;
                //   4. only then tear the player down (ClosePlayer).
                //
                // While either yield is pending the old player is still
                // current and its queued events (Normal priority) can run, so
                // they are ignored for the whole window
                // (_isSwappingPlayerForSelector, set before the first yield and
                // cleared only in the finally). Input runs below Render
                // priority and cannot run in the window, and controller input
                // is routed to the mounted selector first. If the selector
                // completes or is cancelled during a yield, the old player is
                // still closed below before the selection result is returned.
                _isSwappingPlayerForSelector = true;

                try
                {
                    // 1. First yield: the native video window is still visible.
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

                    if (ReferenceEquals(_stremioPlayer, playerToClose))
                    {
                        // 2. Hide the native video window and the controls overlay.
                        playerToClose.HideVideoForClose();

                        // 3. Second yield: lets WPF present the exposed selector.
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }

                    // 4. Tear the player down (the identity is re-checked after
                    // both yields, so a replaced/closed player is never closed
                    // twice).
                    if (ReferenceEquals(_stremioPlayer, playerToClose))
                    {
                        ClosePlayer(restoreHomeFocus: false);
                    }
                }
                finally
                {
                    _isSwappingPlayerForSelector = false;
                }
            }

            return await selection;
        }

        // True from the moment the old player is hidden until it has been
        // closed, while the Source Selector replaces it. Player events and
        // actions that would start a conflicting close / recovery / episode
        // change are ignored in that window.
        private bool _isSwappingPlayerForSelector;

        // =========================================================
        // STREMIO TYPE
        // =========================================================

        // =========================================================
        // MOVIE SYNOPSIS HYDRATION (Slice 19A)
        // =========================================================
        //
        // The Source Selector shows MediaItem.Overview for a movie but never
        // fetches it. Home's catalogue items carry an Overview; Search's
        // Cinemeta catalog results may not. When (and only when) a plain
        // movie has no overview and a Cinemeta (tt...) id, the full
        // CinemetaService.GetMetaAsync("movie", id) description is fetched.
        // Not started for resume, episode transitions, TV/anime (the Episode
        // Browser owns their synopsis), or an item that already has an
        // overview. Fail-soft: unavailable metadata leaves Overview empty
        // (no synopsis is invented) and never blocks source selection.
        private const int MovieOverviewWaitMs = 3000;

        private Task<string> BeginMovieOverviewHydration(
            MediaItem mediaItem,
            ResumePlan resumePlan,
            EpisodeTransition transition)
        {
            if (mediaItem == null ||
                resumePlan != null ||
                transition != null ||
                mediaItem.Season.HasValue ||
                mediaItem.Episode.HasValue ||
                !string.IsNullOrWhiteSpace(mediaItem.Overview) ||
                GetContentType(mediaItem) != ContentType.Movie)
            {
                return null;
            }

            string imdbId = mediaItem.StremioId;

            if (string.IsNullOrWhiteSpace(imdbId) ||
                !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return FetchMovieOverviewAsync(imdbId);
        }

        private static async Task<string> FetchMovieOverviewAsync(string imdbId)
        {
            // GetMetaAsync is fail-soft: null for not-found/unreachable.
            CinemetaMeta meta = await new CinemetaService().GetMetaAsync("movie", imdbId);

            string description = meta?.Description;

            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }

        // Bounded wait: the fetch normally finishes during source discovery,
        // but a slow Cinemeta must never delay the Source Selector by more
        // than MovieOverviewWaitMs - the selector then opens without a
        // synopsis rather than waiting.
        private async Task ApplyMovieOverviewAsync(MediaItem mediaItem, Task<string> overviewTask)
        {
            if (mediaItem == null || overviewTask == null)
            {
                return;
            }

            Task finished = await Task.WhenAny(overviewTask, Task.Delay(MovieOverviewWaitMs));

            if (finished != overviewTask)
            {
                StremioVideoCenterLogger.Info("[SYNOPSIS] movie overview not ready within wait; opening without it");
                return;
            }

            string overview = await overviewTask;

            if (!string.IsNullOrWhiteSpace(overview) &&
                string.IsNullOrWhiteSpace(mediaItem.Overview))
            {
                mediaItem.Overview = overview;
            }
        }

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
