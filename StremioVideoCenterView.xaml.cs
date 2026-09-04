using Playnite.SDK.Controls;
using StremioVideoCenterControls;
using StremioVideoCenter.ViewModels;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StremioVideoCenter.Controls;

namespace StremioVideoCenter
{
    public partial class StremioVideoCenterView : PluginUserControl
    {
        private readonly MediaSection[] sections;
        private LibVlcTestPlayer _libVlcTestPlayer;

        // ---------------------------------------------------------
        // INITIALISE VIEW
        // ---------------------------------------------------------

        public StremioVideoCenterView()
        {
            InitializeComponent();

            // ---------------------------------------------------------
            // INITIALISE LIBVLC TEST PLAYER
            // ---------------------------------------------------------

            _libVlcTestPlayer = new LibVlcTestPlayer();

            HeroMediaFrame.Children.Clear();
            HeroMediaFrame.Children.Add(_libVlcTestPlayer);

            _libVlcTestPlayer.PlayTestVideo(
                "https://streams.videolan.org/misc/unity-samples/BigBuckBunny.avi");

            // ---------------------------------------------------------
            // INITIALISE VIEW MODEL
            // ---------------------------------------------------------

            DataContext = new HomeViewModel();

            // ---------------------------------------------------------
            // MEDIA SECTIONS
            // ---------------------------------------------------------

            sections = new[]
            {
                ContinueWatchingSection,
                MyListSection,
                RecentlyAddedSection,
                TopMoviesSection,
                TopShowsSection,
                TopAnimeSection
            };

            // ---------------------------------------------------------
            // EVENT HANDLERS
            // ---------------------------------------------------------

            Loaded += StremioVideoCenterView_Loaded;

            PreviewKeyDown += StremioVideoCenterView_PreviewKeyDown;

            PreviewMouseWheel += StremioVideoCenterView_PreviewMouseWheel;
        }



        // =========================================================
        // INITIAL LOAD
        // =========================================================

        private async void StremioVideoCenterView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            // ---------------------------------------------------------
            // LOAD HERO BACKDROP
            // ---------------------------------------------------------

            if (DataContext is HomeViewModel vm &&
                vm.CurrentHeroItem != null)
            {
                await vm.CurrentHeroItem.LoadBackdropAsync();
            }

            // ---------------------------------------------------------
            // INITIAL FOCUS
            // ---------------------------------------------------------

            await Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    FocusFirstAvailableSection();
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

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

                // Focus the first card without scrolling the page down.
                section.FocusFirstCard();

                // Keep the extension positioned at the top on startup.
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
                // -------------------------------------------------
                // LEFT
                // -------------------------------------------------

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

                // -------------------------------------------------
                // RIGHT
                // -------------------------------------------------

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

                // -------------------------------------------------
                // DOWN
                // HERO -> FIRST MEDIA SECTION
                // -------------------------------------------------

                if (e.Key == Key.Down)
                {
                    if (sections.Length > 0 &&
                        sections[0] != null)
                    {
                        // Focus the first card without moving
                        // the main page vertically.
                        sections[0].FocusFirstCard();

                        e.Handled = true;
                    }

                    return;
                }

                // -------------------------------------------------
                // UP
                // HERO -> HOME
                // -------------------------------------------------

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

            // =========================================================
            // HOME LEFT -> HERO MY LIST
            // =========================================================

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

            // -----------------------------------------------------
            // LEFT / RIGHT
            // -----------------------------------------------------

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

                currentSection.MoveHorizontal(direction);

                e.Handled = true;

                return;
            }

            // -----------------------------------------------------
            // UP / DOWN
            // -----------------------------------------------------

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

            if (!targetSection.IsFirstCardVisible(MainScrollViewer))
            {
                MainScrollViewer.ScrollToVerticalOffset(
                    GetSectionOffset(targetSection));
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
                MainScrollViewer.VerticalOffset - amount;

            double maxOffset =
                Math.Max(
                    0,
                    MainScrollViewer.ExtentHeight -
                    MainScrollViewer.ViewportHeight);

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
                        MainScrollViewer.ExtentHeight -
                        MainScrollViewer.ViewportHeight);

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