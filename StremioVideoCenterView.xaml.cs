using Playnite.SDK.Controls;
using StremioVideoCenterControls;
using StremioVideoCenter.ViewModels;
using StremioVideoCenter.Services;
using StremioVideoCenter.Models;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StremioVideoCenter.Controls;

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

        private LibVlcPlayer _stremioPlayer;

        // =========================================================
        // STARTUP STATE
        // =========================================================

        private bool _startupSequenceStarted;


        // =========================================================
        // TMDB CREDENTIAL
        // =========================================================

        private readonly string _tmdbAccessToken;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterView(
            string tmdbAccessToken = null)
        {
            InitializeComponent();

            // =========================================================
            // STORE TMDB CREDENTIAL
            // =========================================================

            _tmdbAccessToken =
                tmdbAccessToken;


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
            // TEMPORARY STREMIO CONNECTION TEST
            // =========================================================

            await PlayStremioTestStreamAsync();
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
        // TEMPORARY STREMIO PLAYBACK TEST
        // =========================================================

        private async Task PlayStremioTestStreamAsync()
        {
            try
            {
                var addon =
                    new StremioAddonService(
                        "http://127.0.0.1:7000");

                string streamUrl =
                    await addon.GetFirstPlayableUrlAsync(
                        "movie",
                        "tt1254207");

                if (string.IsNullOrWhiteSpace(streamUrl))
                {
                    MessageBox.Show(
                        "Stremio returned no playable URL.");

                    return;
                }

                if (_stremioPlayer == null)
                {
                    _stremioPlayer =
                        new LibVlcPlayer();

                    PlaybackTestHost.Children.Add(
                        _stremioPlayer);
                }

                PlaybackTestHost.Visibility =
                    Visibility.Visible;

                _stremioPlayer.PlayVideo(
                    streamUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Stremio playback test failed:\n\n" +
                    ex.Message);
            }
        }
    }
    
}