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
        // STARTUP STATE
        // =========================================================

        private bool _startupSequenceStarted;


        // =========================================================
        // TMDB CREDENTIAL
        // =========================================================

        private readonly string _tmdbAccessToken;


        // =========================================================
        // REAL-DEBRID CREDENTIAL
        // =========================================================

        private readonly string _realDebridApiToken;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterView(
            string tmdbAccessToken = null,
            string realDebridApiToken = null)
        {
            InitializeComponent();

            // =========================================================
            // STORE CREDENTIALS
            // =========================================================

            _tmdbAccessToken =
                tmdbAccessToken;

            _realDebridApiToken =
                realDebridApiToken;


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

            try
            {
                string type =
                    GetStremioType(
                        mediaItem);

                var addon =
                    new StremioAddonService(
                        "http://127.0.0.1:7000");

                List<StremioStream> streams =
                    await addon.GetStreamsAsync(
                        type,
                        mediaItem.StremioId);

                if (streams == null ||
                    streams.Count == 0)
                {
                    MessageBox.Show(
                        "Stremio returned no playable streams for:\n\n" +
                        mediaItem.Title);

                    return;
                }

                // =========================================================
                // RESOLVE A PLAYABLE SOURCE
                // =========================================================
                //
                // Try streams in order until one resolves. Direct-URL streams
                // (our test addon's path) resolve immediately with no
                // Real-Debrid involvement. InfoHash streams need Real-Debrid,
                // which may not be configured - that surfaces as a per-stream
                // failure here and we just move to the next stream rather
                // than failing playback outright. ExternalUrl-only streams
                // are skipped before ever reaching the resolver; they are not
                // supported for in-app playback this phase.

                RealDebridService realDebrid =
                    string.IsNullOrWhiteSpace(
                        _realDebridApiToken)
                        ? null
                        : new RealDebridService(
                              _realDebridApiToken);

                var resolver =
                    new StreamResolverService(
                        realDebrid);

                PlaybackSource source = null;

                foreach (StremioStream stream in streams)
                {
                    if (stream == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(stream.Url) &&
                        string.IsNullOrWhiteSpace(stream.InfoHash))
                    {
                        // ExternalUrl-only (or empty) stream - unsupported
                        // for in-app playback this phase.
                        continue;
                    }

                    try
                    {
                        source =
                            await resolver.ResolveStreamAsync(
                                stream);

                        if (source != null &&
                            !string.IsNullOrWhiteSpace(source.Url))
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // This particular stream couldn't be resolved (e.g.
                        // needs Real-Debrid but none is configured, or
                        // Real-Debrid failed for this specific torrent).
                        // Try the next stream instead of failing outright.
                        source = null;
                    }
                }

                if (source == null ||
                    string.IsNullOrWhiteSpace(source.Url))
                {
                    MessageBox.Show(
                        "None of the available streams could be resolved for:\n\n" +
                        mediaItem.Title);

                    return;
                }

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
                    source.Url);
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
