using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Events;
using StremioVideoCenterControls;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenter.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
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
        // STREMIO ADDON URLS (newline-delimited)
        // =========================================================

        private readonly string _stremioAddonUrls;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================
        //
        // Takes a single configuration object rather than individually-
        // growing positional parameters - see
        // Models/StremioVideoCenterConfiguration.cs. Unpacked into private
        // fields here (rather than stored as the whole object and threaded
        // through every method) so the rest of this class's existing
        // internal references stay exactly as they were.

        public StremioVideoCenterView(
            StremioVideoCenterConfiguration configuration = null)
        {
            InitializeComponent();

            // =========================================================
            // STORE CREDENTIALS
            // =========================================================

            configuration = configuration ?? new StremioVideoCenterConfiguration();

            _tmdbAccessToken =
                configuration.TmdbApiKey;

            _omdbApiKey =
                configuration.OmdbApiKey;

            _realDebridApiToken =
                configuration.RealDebridApiKey;

            _stremioAddonUrls =
                configuration.StremioAddonUrls;


            // =========================================================
            // CREATE HOME VIEW MODEL
            // =========================================================

            DataContext =
                new HomeViewModel();


            // =========================================================
            // REGISTER MEDIA SECTIONS
            // =========================================================

            sections =
                new[]
                {
                    ContinueWatchingSection,
                    MyListSection,
                    RecentlyAddedSection,
                    TopMoviesSection,
                    TopShowsSection,
                    TopAnimeSection
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
            // REGISTER VIEW EVENTS
            // =========================================================

            Loaded +=
                StremioVideoCenterView_Loaded;

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
            // =========================================================
            // PREVENT DUPLICATE STARTUP SEQUENCES
            // =========================================================

            if (_startupSequenceStarted)
            {
                return;
            }

            _startupSequenceStarted = true;


            // =========================================================
            // LOAD TMDB HERO RECOMMENDATIONS
            // =========================================================

            if (!string.IsNullOrWhiteSpace(
                    _tmdbAccessToken) &&
                DataContext is HomeViewModel vm)
            {
                try
                {
                    TmdbService tmdbService =
                        new TmdbService(
                            _tmdbAccessToken);

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
            // UPDATE CAROUSEL INDICATORS
            // =========================================================

            UpdateHeroIndicators();


            // =========================================================
            // INITIAL FOCUS
            // =========================================================

            await Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FocusFirstAvailableSection();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);


            // =========================================================
            // START BACKDROP ROTATION
            // =========================================================

            await StartHeroBackdropRotationAsync();



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
        // Backdrop-only Hero mode.
        //
        // Each recommendation remains visible for 15 seconds.
        // Manual navigation starts a fresh 15-second display period.
        // =========================================================

        private async Task StartHeroBackdropRotationAsync()
        {
            HomeViewModel vm =
                DataContext as HomeViewModel;

            if (vm == null ||
                vm.CurrentHeroItem == null)
            {
                return;
            }

            HeroItem heroItem =
                vm.CurrentHeroItem;


            // =========================================================
            // HERO DISPLAY PERIOD
            // =========================================================

            await Task.Delay(
                TimeSpan.FromSeconds(15));


            // =========================================================
            // ONLY ADVANCE IF THIS HERO IS STILL ACTIVE
            // =========================================================

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
            HeroItem heroItem)
        {
            await LoadCurrentHeroBackdropAsync(
                heroItem);

            await StartHeroBackdropRotationAsync();
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
                _ =
                    LoadHeroAndRestartRotationAsync(
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
                _ =
                    LoadHeroAndRestartRotationAsync(
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
        // FOCUS FIRST AVAILABLE SECTION
        // =========================================================

        private void FocusFirstAvailableSection()
        {
            foreach (MediaSection section in sections)
            {
                if (section == null)
                {
                    continue;
                }

                if (section.Items == null)
                {
                    continue;
                }

                section.FocusFirstCard();

                MainScrollViewer.ScrollToVerticalOffset(0);

                return;
            }
        }


        // =========================================================
        // KEYBOARD / CONTROLLER NAVIGATION
        // =========================================================

        private void StremioVideoCenterView_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            // =====================================================
            // HERO CAROUSEL BUTTONS
            // =====================================================

            if (HeroPreviousButton.IsKeyboardFocusWithin ||
                HeroNextButton.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Left)
                {
                    ChangeHero(-1);

                    HeroPreviousButton.Focus();

                    e.Handled = true;

                    return;
                }

                if (e.Key == Key.Right)
                {
                    ChangeHero(1);

                    HeroNextButton.Focus();

                    e.Handled = true;

                    return;
                }

                if (e.Key == Key.Down)
                {
                    HeroWatchNowButton.Focus();

                    e.Handled = true;

                    return;
                }

                if (e.Key == Key.Up)
                {
                    HomeButton.Focus();

                    e.Handled = true;

                    return;
                }
            }


            // =====================================================
            // HERO INFORMATION BUTTONS
            // =====================================================

            if (HeroWatchNowButton.IsKeyboardFocusWithin ||
                HeroMyListButton.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Left)
                {
                    if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        HeroWatchNowButton.Focus();

                        e.Handled = true;
                    }
                    else if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        SearchButton.Focus();

                        e.Handled = true;
                    }

                    return;
                }

                if (e.Key == Key.Right)
                {
                    if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        HeroMyListButton.Focus();

                        e.Handled = true;
                    }
                    else if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        HomeButton.Focus();

                        e.Handled = true;
                    }

                    return;
                }

                if (e.Key == Key.Up)
                {
                    HeroPreviousButton.Focus();

                    e.Handled = true;

                    return;
                }

                if (e.Key == Key.Down)
                {
                    if (sections.Length > 0 &&
                        sections[0] != null)
                    {
                        sections[0].FocusFirstCard();

                        e.Handled = true;
                    }

                    return;
                }
            }


            // =====================================================
            // TOP NAVIGATION -> HERO
            // =====================================================

            if (e.Key == Key.Down)
            {
                if (HomeButton.IsKeyboardFocusWithin ||
                    MoviesButton.IsKeyboardFocusWithin ||
                    TvShowsButton.IsKeyboardFocusWithin ||
                    AnimeButton.IsKeyboardFocusWithin ||
                    NavigationMyListButton.IsKeyboardFocusWithin ||
                    HistoryButton.IsKeyboardFocusWithin ||
                    SearchButton.IsKeyboardFocusWithin)
                {
                    HeroWatchNowButton.Focus();

                    e.Handled = true;

                    return;
                }
            }


            // =====================================================
            // SEARCH -> HERO
            // =====================================================

            if (SearchButton.IsKeyboardFocusWithin &&
                e.Key == Key.Right)
            {
                HeroWatchNowButton.Focus();

                e.Handled = true;

                return;
            }


            // =====================================================
            // HOME LEFT -> HERO MY LIST
            // =====================================================

            if (HomeButton.IsKeyboardFocusWithin &&
                e.Key == Key.Left)
            {
                HeroMyListButton.Focus();

                e.Handled = true;

                return;
            }


            // =====================================================
            // MEDIA NAVIGATION
            // =====================================================

            MediaSection currentSection =
                GetFocusedSection();

            if (e.Key == Key.Left ||
                e.Key == Key.Right)
            {
                if (currentSection == null)
                {
                    return;
                }

                int direction =
                    e.Key == Key.Left
                        ? -1
                        : 1;

                currentSection.MoveHorizontal(
                    direction);

                e.Handled = true;

                return;
            }

            if (e.Key == Key.Up ||
                e.Key == Key.Down)
            {
                if (currentSection == null)
                {
                    return;
                }

                MoveVertical(
                    currentSection,
                    e.Key);

                e.Handled = true;

                return;
            }
        }


        // =========================================================
        // VERTICAL NAVIGATION
        // =========================================================

        private void MoveVertical(
            MediaSection currentSection,
            Key key)
        {
            int currentIndex =
                Array.IndexOf(
                    sections,
                    currentSection);

            if (currentIndex < 0)
            {
                return;
            }

            int direction =
                key == Key.Up
                    ? -1
                    : 1;

            if (key == Key.Up &&
                currentIndex == 0)
            {
                HeroWatchNowButton.Focus();

                return;
            }

            int targetIndex =
                currentIndex + direction;

            if (targetIndex < 0 ||
                targetIndex >= sections.Length)
            {
                return;
            }

            MediaSection targetSection =
                sections[targetIndex];

            if (targetSection == null ||
                targetSection.Items == null)
            {
                return;
            }

            targetSection.FocusFirstCard();

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
            // Source selector owns controller input while a selection is
            // in progress - checked before the player, since a selector and
            // a player are never active at the same time, but checking
            // selector first is harmless either way and keeps this
            // consistent with "most specific overlay wins" ordering.
            if (_activeSourceSelector != null)
            {
                return _activeSourceSelector.HandleControllerInput(button);
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
                    return HandleHomeControllerDirection(Key.Left);

                case ControllerInput.DPadRight:
                    return HandleHomeControllerDirection(Key.Right);

                case ControllerInput.DPadUp:
                    return HandleHomeControllerDirection(Key.Up);

                case ControllerInput.DPadDown:
                    return HandleHomeControllerDirection(Key.Down);

                default:
                    return false;
            }
        }

        private bool SelectFocusedMediaCard()
        {
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

        private bool HandleHomeControllerDirection(
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
                    HeroPreviousButton.Focus();
                    return true;
                }

                if (key == Key.Down &&
                    sections.Length > 0 &&
                    sections[0] != null)
                {
                    sections[0].FocusFirstCard();
                    return true;
                }
            }

            if (key == Key.Down)
            {
                if (HomeButton.IsKeyboardFocusWithin ||
                    MoviesButton.IsKeyboardFocusWithin ||
                    TvShowsButton.IsKeyboardFocusWithin ||
                    AnimeButton.IsKeyboardFocusWithin ||
                    NavigationMyListButton.IsKeyboardFocusWithin ||
                    HistoryButton.IsKeyboardFocusWithin ||
                    SearchButton.IsKeyboardFocusWithin)
                {
                    HeroWatchNowButton.Focus();
                    return true;
                }
            }

            if (SearchButton.IsKeyboardFocusWithin &&
                key == Key.Right)
            {
                HeroWatchNowButton.Focus();
                return true;
            }

            if (HomeButton.IsKeyboardFocusWithin &&
                key == Key.Left)
            {
                HeroMyListButton.Focus();
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
        // MEDIA CARD SELECTION
        // =========================================================

        private async void MediaSection_MediaSelected(
            object sender,
            MediaItemSelectedEventArgs e)
        {
            if (e == null ||
                e.Item == null)
            {
                return;
            }

            await PlaySelectedMediaAsync(
                e.Item);
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

            if (string.IsNullOrWhiteSpace(
                    mediaItem.StremioId))
            {
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
                // =========================================================
                // DISCOVER + AGGREGATE + NORMALISE
                // =========================================================
                //
                // ContentRequest.ExplicitStremioId is used directly rather
                // than ForMovie/ForTvEpisode, which both require a
                // TmdbDetailsItem/TmdbEpisodeDetails this method doesn't
                // have - MediaItem.StremioId is already the correct Stremio
                // content id, so BuildStremioId() can use it verbatim with
                // no TMDB call added to this path.

                var addonRegistry =
                    StremioAddonRegistry.FromConfiguredUrls(
                        _stremioAddonUrls);

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
                // Nothing constructs a MediaItem with these populated yet
                // (episode browsing UI is a separate, later slice), so this
                // branch is not yet reachable in practice - it exists so
                // the pipeline is ready the moment something does.
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

                // Nothing discovered at all (no addon had anything, or the
                // request couldn't even build a Stremio id) - identical
                // behaviour to before this slice, no selector shown.
                if (set.Sources.Count == 0 &&
                    set.Unresolved.Count == 0)
                {
                    MessageBox.Show(
                        "Stremio returned no playable streams for:\n\n" +
                        mediaItem.Title);

                    return;
                }

                // set.Unresolved is preserved on the set itself for future
                // diagnostics. Not surfaced to the user this slice, and
                // deliberately not passed to the selector, which has no
                // concept of unresolved entries by design.
                List<NormalizedPlaybackSource> normalizedSources =
                    normalizer.NormalizeAll(
                        set.Sources);

                // =========================================================
                // SHOW THE SOURCE SELECTOR
                // =========================================================
                //
                // Populate() is called regardless of count, including zero -
                // SourceSelectorView already handles the empty-source case
                // (a "No playable sources found." message with a Back
                // action) by design. That is exactly the case where
                // set.Sources is empty but set.Unresolved is not.

                NormalizedPlaybackSource selected =
                    await ShowSourceSelectorAsync(
                        normalizedSources,
                        mediaItem);

                if (selected == null)
                {
                    // Selection cancelled - already torn down and refocused
                    // by ShowSourceSelectorAsync itself.
                    return;
                }

                // =========================================================
                // PLAY THE SELECTED SOURCE
                // =========================================================
                //
                // Player creation/behaviour is completely unchanged from
                // before this slice - only where the URL comes from has
                // changed.

                if (_stremioPlayer == null)
                {
                    _stremioPlayer =
                        new MpvPlayer();

                    _stremioPlayer.CloseRequested +=
                        StremioPlayer_CloseRequested;

                    // Playback stays inside the Video Center.
                    // The temporary MPV isolation window is deliberately gone.
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
        // GET CONTENT TYPE
        // =========================================================
        //
        // Maps MediaItem.Type to the ContentType enum needed to build a
        // ContentRequest. Mirrors GetStremioType's existing TV/TV Show/
        // Series matching, and additionally recognises "Anime" ->
        // ContentType.Anime. GetStremioType itself has no Anime case at
        // all (it falls through to "movie" for anime titles) - this
        // corrects that for anything going through this new path, per
        // explicit approval for this slice. GetStremioType is no longer
        // called anywhere but is left in place rather than removed, since
        // removing it wasn't part of what was asked for this slice.

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
        //
        // Creates a fresh SourceSelectorView, shows it in
        // SourceSelectorHost, and bridges its SourceSelected/
        // SelectionCancelled events into an awaitable Task via
        // TaskCompletionSource - SourceSelectorView itself stays purely
        // event-based and is not modified. Returns the selected source, or
        // null if the user cancelled. Tears the host down and restores
        // browsing focus before returning either way.

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

            // mediaItem is passed through only to populate the selector's
            // header media-context line (Slice 10) - it plays no role in
            // discovery/aggregation/normalisation, all of which already
            // completed before this method is called.
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

        // Tears down _stremioPlayer/PlaybackTestHost and returns focus to the
        // home screen. Called both when the player itself requests a close and
        // when a play attempt fails outright - a failed PlayVideoAsync() call
        // previously left PlaybackTestHost visible (its black, ZIndex=1000
        // overlay) and _stremioPlayer non-null, which also meant
        // HandleControllerInput kept routing all controller input into the
        // broken player instead of back to the home screen.
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
