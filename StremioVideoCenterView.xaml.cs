using Playnite.SDK.Controls;
using StremioVideoCenterControls;
using StremioVideoCenter.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        // LIBVLC PLAYER
        // =========================================================

        private LibVlcTestPlayer _libVlcTestPlayer;


        // =========================================================
        // STARTUP STATE
        // =========================================================

        // Prevents the startup sequence from being triggered more
        // than once if the Loaded event fires multiple times.
        private bool _startupSequenceStarted;


        // =========================================================
        // INITIALISE VIEW
        // =========================================================

        public StremioVideoCenterView()
        {
            InitializeComponent();


            // =========================================================
            // INITIALISE LIBVLC PLAYER
            // =========================================================

            _libVlcTestPlayer =
                new LibVlcTestPlayer();


            HeroMediaFrame.Children.Clear();

            HeroMediaFrame.Children.Add(
                _libVlcTestPlayer);


            // =========================================================
            // INITIALISE VIEW MODEL
            // =========================================================

            DataContext =
                new HomeViewModel();


            // =========================================================
            // MEDIA SECTIONS
            // =========================================================

            sections = new[]
            {
                ContinueWatchingSection,
                MyListSection,
                RecentlyAddedSection,
                TopMoviesSection,
                TopShowsSection,
                TopAnimeSection
            };


            // =========================================================
            // EVENT HANDLERS
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
            // LOAD HERO BACKDROP
            // =========================================================

            if (DataContext is HomeViewModel vm &&
                vm.CurrentHeroItem != null)
            {
                await vm.CurrentHeroItem.LoadBackdropAsync();
            }


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
            // START HERO MEDIA SEQUENCE
            // =========================================================

            await StartHeroMediaSequence();
        }


        // =========================================================
        // HERO MEDIA STARTUP SEQUENCE
        // =========================================================
        //
        // Timeline:
        //
        // 0.00s
        // Backdrop visible.
        //
        // 0.00s - 0.80s
        // Backdrop remains fully visible.
        //
        // 0.80s
        // Trailer begins playback.
        //
        // 0.80s - 1.50s
        // Trailer/video surface fades into view.
        //
        // 1.50s
        // Trailer fully visible.
        //
        // =========================================================

        private async Task StartHeroMediaSequence()
        {
            // =========================================================
            // ENSURE VIDEO SURFACE STARTS INVISIBLE
            // =========================================================

            _libVlcTestPlayer.Opacity = 0;


            // =========================================================
            // BACKDROP DISPLAY PERIOD
            //
            // The backdrop gets approximately 800ms of uninterrupted
            // visibility before trailer playback begins.
            // =========================================================

            await Task.Delay(800);


            // =========================================================
            // START TRAILER
            // =========================================================

            _libVlcTestPlayer.PlayTestVideo(
                "https://streams.videolan.org/misc/unity-samples/BigBuckBunny.avi");


            // =========================================================
            // ALLOW LIBVLC TO INITIALISE
            //
            // Give LibVLC a short moment to create its video output
            // before starting the visual transition.
            // =========================================================

            await Task.Delay(100);


            // =========================================================
            // FADE VIDEO INTO VIEW
            //
            // Target duration:
            // approximately 600ms.
            //
            // Combined with the 800ms backdrop period and the small
            // LibVLC startup allowance, this keeps the transition
            // around our 1.5 second target.
            // =========================================================

            DoubleAnimation fadeIn =
                new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration =
                        new Duration(
                            TimeSpan.FromMilliseconds(600)),
                    EasingFunction =
                        new QuadraticEase
                        {
                            EasingMode =
                                EasingMode.EaseInOut
                        }
                };


            // =========================================================
            // START VIDEO SURFACE FADE
            // =========================================================

            _libVlcTestPlayer.BeginAnimation(
                UIElement.OpacityProperty,
                fadeIn);


            // =========================================================
            // WAIT FOR TRANSITION TO COMPLETE
            // =========================================================

            await Task.Delay(600);


            // =========================================================
            // ENSURE FINAL OPACITY
            // =========================================================

            _libVlcTestPlayer.BeginAnimation(
                UIElement.OpacityProperty,
                null);

            _libVlcTestPlayer.Opacity = 1;
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


                // =====================================================
                // FOCUS FIRST CARD
                // =====================================================

                section.FocusFirstCard();


                // =====================================================
                // KEEP PAGE AT TOP
                // =====================================================

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
            // HERO NAVIGATION
            // =====================================================

            if (HeroWatchNowButton.IsKeyboardFocusWithin ||
                HeroMyListButton.IsKeyboardFocusWithin)
            {
                // =================================================
                // LEFT
                // =================================================

                if (e.Key == Key.Left)
                {
                    if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        // HERO MY LIST -> WATCH NOW

                        HeroWatchNowButton.Focus();

                        e.Handled = true;
                    }
                    else if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        // WATCH NOW -> SEARCH

                        SearchButton.Focus();

                        e.Handled = true;
                    }

                    return;
                }


                // =================================================
                // RIGHT
                // =================================================

                if (e.Key == Key.Right)
                {
                    if (HeroWatchNowButton.IsKeyboardFocusWithin)
                    {
                        // WATCH NOW -> HERO MY LIST

                        HeroMyListButton.Focus();

                        e.Handled = true;
                    }
                    else if (HeroMyListButton.IsKeyboardFocusWithin)
                    {
                        // HERO MY LIST -> HOME

                        HomeButton.Focus();

                        e.Handled = true;
                    }

                    return;
                }


                // =================================================
                // DOWN
                // HERO -> FIRST MEDIA SECTION
                // =================================================

                if (e.Key == Key.Down)
                {
                    if (sections.Length > 0 &&
                        sections[0] != null)
                    {
                        // Focus first card without moving
                        // the main page vertically.

                        sections[0].FocusFirstCard();

                        e.Handled = true;
                    }

                    return;
                }


                // =================================================
                // UP
                // HERO -> HOME
                // =================================================

                if (e.Key == Key.Up)
                {
                    HomeButton.Focus();

                    e.Handled = true;

                    return;
                }

                return;
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
                    // ANY TOP NAV BUTTON -> HERO WATCH NOW

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


            // =====================================================
            // LEFT / RIGHT
            // =====================================================

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


            // =====================================================
            // UP / DOWN
            // =====================================================

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


            // =====================================================
            // FIRST MEDIA SECTION -> HERO
            // =====================================================

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


            if (targetSection == null)
            {
                return;
            }


            if (targetSection.Items == null)
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
    }
}