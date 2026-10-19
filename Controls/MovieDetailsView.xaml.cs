using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StremioVideoCenterControls
{
    // =============================================================
    // MOVIE DETAILS (Slice 19B)
    // =============================================================
    //
    // Dedicated details page shown between a movie selection and the
    // Source Selector. It owns presentation only: it never reads
    // MyListService or starts discovery/playback itself - it raises
    // WatchNowRequested / MyListToggleRequested / Closed and the main view
    // (the single navigation owner) acts on them, exactly like
    // EpisodeBrowserView.
    //
    // Data: the MediaItem already carries whatever the originating
    // catalogue supplied (title, poster, year, rating, overview...). It is
    // shown immediately. LoadAsync then asks the existing CinemetaService
    // for the full movie meta (fail-soft, never throws) and fills in only
    // what is missing/extra. Nothing is invented: a field with no data is
    // simply not shown.
    public partial class MovieDetailsView : UserControl
    {
        // Poster column is dropped below this width so the text column
        // keeps a readable measure. One adaptive layout, no device modes.
        private const double NarrowWidthThreshold = 720;

        private MediaItem _movie;
        private bool _isInMyList;
        private bool _isBusy;
        private bool _isUnloaded;
        private int _loadSequence;

        public event EventHandler WatchNowRequested;
        public event EventHandler MyListToggleRequested;
        public event EventHandler Closed;

        public MovieDetailsView()
        {
            InitializeComponent();

            // Mouse click-and-drag scrolling (Slice 20 Stage 3).
            DragScroll.Attach(ContentScrollViewer, DragScrollAxis.Vertical);
            Unloaded += (s, e) => { _isUnloaded = true; };
            Loaded += (s, e) => { _isUnloaded = false; };
        }

        public MediaItem Movie
        {
            get { return _movie; }
        }

        // =========================================================
        // LOADING
        // =========================================================

        public async Task LoadAsync(MediaItem movie, CinemetaService cinemetaService)
        {
            if (movie == null)
            {
                return;
            }

            _movie = movie;
            int sequence = ++_loadSequence;

            ApplyMediaItem(movie);
            ApplyNarrowLayout(ActualWidth);

            if (cinemetaService == null || !LooksLikeImdbId(movie.StremioId))
            {
                return;
            }

            CinemetaMeta meta = null;

            try
            {
                meta = await cinemetaService.GetMetaAsync("movie", movie.StremioId);
            }
            catch (Exception ex)
            {
                // GetMetaAsync is documented never to throw; this only
                // guards an unexpected change. Details simply keep what the
                // MediaItem already carries.
                System.Diagnostics.Debug.WriteLine(
                    "[MovieDetails] Metadata load failed: " + ex.GetType().Name);
            }

            // Superseded by a newer LoadAsync, or the view is gone.
            if (sequence != _loadSequence || _isUnloaded)
            {
                return;
            }

            if (meta == null)
            {
                if (string.IsNullOrWhiteSpace(movie.Overview))
                {
                    ShowStatus("More details aren't available for this title right now.");
                }

                return;
            }

            ApplyMeta(movie, meta);
        }

        private static bool LooksLikeImdbId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                   id.StartsWith("tt", StringComparison.OrdinalIgnoreCase);
        }

        // First paint: only what the MediaItem already has.
        private void ApplyMediaItem(MediaItem movie)
        {
            TitleText.Text = !string.IsNullOrWhiteSpace(movie.Title) ? movie.Title : "Untitled";

            SetMeta(movie.Year, movie.Rating, null);
            SetGenres(movie.Genres);
            SetSynopsis(movie.Overview);
            SetImage(PosterImage, movie.PosterPath);
            SetImage(BackdropImage, movie.BackdropUrl);
            ClearStatus();
            DetailsPanel.Children.Clear();
            DetailsPanel.Visibility = Visibility.Collapsed;
        }

        // Enrichment: fill gaps, add extras. The MediaItem's own values
        // win where present so the page never contradicts the card the
        // user just selected.
        private void ApplyMeta(MediaItem movie, CinemetaMeta meta)
        {
            if (string.IsNullOrWhiteSpace(movie.Title) && !string.IsNullOrWhiteSpace(meta.Name))
            {
                TitleText.Text = meta.Name;
            }

            string year = !string.IsNullOrWhiteSpace(movie.Year)
                ? movie.Year
                : FirstNonEmpty(meta.Year, meta.ReleaseInfo);

            string rating = !string.IsNullOrWhiteSpace(movie.Rating)
                ? movie.Rating
                : meta.ImdbRating;

            SetMeta(year, rating, FormatRuntime(meta.Runtime));

            if (movie.Genres == null || movie.Genres.Count == 0)
            {
                SetGenres(meta.Genres);
            }

            // The MediaItem's Overview comes from the originating catalogue
            // entry (Home/Search/Hero), which can be a shortened blurb. The
            // full movie meta is the authoritative description, so it
            // replaces the catalogue text when it is the more complete one
            // (longer). Nothing is cut or invented: whichever text is the
            // fuller of the two is shown whole. The result is shared back on
            // the MediaItem so the Source Selector header reuses it without
            // another request.
            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                string full = meta.Description.Trim();
                string current = (movie.Overview ?? string.Empty).Trim();

                if (full.Length > current.Length)
                {
                    movie.Overview = full;
                    SetSynopsis(full);
                }
            }

            if (string.IsNullOrWhiteSpace(movie.PosterPath))
            {
                SetImage(PosterImage, meta.Poster);
            }

            if (string.IsNullOrWhiteSpace(movie.BackdropUrl))
            {
                SetImage(BackdropImage, meta.Background);
            }

            BuildDetailRows(meta);

            if (string.IsNullOrWhiteSpace(movie.Overview))
            {
                ShowStatus("No synopsis is available for this title.");
            }
            else
            {
                ClearStatus();
            }
        }

        // =========================================================
        // PRESENTATION HELPERS
        // =========================================================

        private void SetMeta(string year, string rating, string runtime)
        {
            List<string> parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(year))
            {
                parts.Add(year.Trim());
            }

            if (!string.IsNullOrWhiteSpace(runtime))
            {
                parts.Add(runtime);
            }

            if (!string.IsNullOrWhiteSpace(rating))
            {
                parts.Add("★ " + rating.Trim());
            }

            if (parts.Count == 0)
            {
                MetaText.Visibility = Visibility.Collapsed;
                MetaText.Text = string.Empty;
                return;
            }

            MetaText.Text = string.Join("   ·   ", parts);
            MetaText.Visibility = Visibility.Visible;
        }

        private void SetGenres(IEnumerable<string> genres)
        {
            GenrePanel.Children.Clear();

            List<string> list = genres == null
                ? new List<string>()
                : genres.Where(g => !string.IsNullOrWhiteSpace(g)).Take(6).ToList();

            if (list.Count == 0)
            {
                GenrePanel.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (string genre in list)
            {
                GenrePanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(0, 0, 8, 8),
                    Child = new TextBlock
                    {
                        Text = genre.Trim(),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                        FontSize = 13
                    }
                });
            }

            GenrePanel.Visibility = Visibility.Visible;
        }

        private void SetSynopsis(string overview)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                SynopsisHeading.Visibility = Visibility.Collapsed;
                SynopsisText.Visibility = Visibility.Collapsed;
                SynopsisText.Text = string.Empty;
                return;
            }

            SynopsisText.Text = overview.Trim();
            SynopsisHeading.Visibility = Visibility.Visible;
            SynopsisText.Visibility = Visibility.Visible;
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }

        private void ClearStatus()
        {
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private void BuildDetailRows(CinemetaMeta meta)
        {
            DetailsPanel.Children.Clear();

            AddDetailRow("Director", JoinList(meta.Director, 3));
            AddDetailRow("Writer", JoinList(meta.Writer, 3));
            AddDetailRow("Cast", JoinList(meta.Cast, 8));
            AddDetailRow("Country", meta.Country);

            DetailsPanel.Visibility = DetailsPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddDetailRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            StackPanel row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            row.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63)),
                FontSize = 12,
                FontWeight = FontWeights.Bold
            });

            row.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            DetailsPanel.Children.Add(row);
        }

        private static string JoinList(IEnumerable<string> values, int max)
        {
            if (values == null)
            {
                return null;
            }

            List<string> list = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Take(max)
                .ToList();

            return list.Count == 0 ? null : string.Join(", ", list);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        // Cinemeta runtime is a free-form string such as "142 min".
        private static string FormatRuntime(string runtime)
        {
            return string.IsNullOrWhiteSpace(runtime) ? null : runtime.Trim();
        }

        private static void SetImage(Image target, string url)
        {
            target.Source = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Uri uri;

                if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                {
                    return;
                }

                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                target.Source = bitmap;
            }
            catch (Exception ex)
            {
                // A bad artwork URL must never break the page; the poster
                // placeholder glyph remains visible.
                System.Diagnostics.Debug.WriteLine(
                    "[MovieDetails] Image load failed: " + ex.GetType().Name);
            }
        }

        // =========================================================
        // STATE FROM THE HOST
        // =========================================================

        public void SetMyListState(bool isInMyList)
        {
            _isInMyList = isInMyList;
            MyListButton.Content = isInMyList ? "✓ IN MY LIST" : "+ MY LIST";
            MyListButton.ToolTip = isInMyList ? "Remove from My List" : "Add to My List";
        }

        // While the host is resolving sources after Watch Now, further
        // activation is ignored (prevents a double Watch Now from starting
        // two discoveries) but Back/Close still work.
        public void SetBusy(bool busy)
        {
            _isBusy = busy;
            WatchNowButton.Content = busy ? "LOADING SOURCES…" : "▶  WATCH NOW";
            WatchNowButton.Opacity = busy ? 0.7 : 1.0;
        }

        public void FocusPrimary()
        {
            FocusElement(WatchNowButton);
        }

        private void FocusElement(IInputElement element)
        {
            if (element == null)
            {
                return;
            }

            Keyboard.Focus(element);

            FrameworkElement fe = element as FrameworkElement;
            if (fe != null)
            {
                fe.BringIntoView();
            }
        }

        // =========================================================
        // ADAPTIVE LAYOUT
        // =========================================================

        private void MovieDetailsView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyNarrowLayout(e.NewSize.Width);
        }

        private void ApplyNarrowLayout(double width)
        {
            if (width <= 0)
            {
                return;
            }

            bool narrow = width < NarrowWidthThreshold;

            PosterBorder.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(PosterBorder, 0);
        }

        // =========================================================
        // INPUT
        // =========================================================

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void WatchNowButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseWatchNow();
        }

        private void MyListButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseMyListToggle();
        }

        private void RaiseClosed()
        {
            EventHandler handler = Closed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseWatchNow()
        {
            if (_isBusy)
            {
                return;
            }

            EventHandler handler = WatchNowRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseMyListToggle()
        {
            EventHandler handler = MyListToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void MovieDetailsView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.Back:
                    RaiseClosed();
                    e.Handled = true;
                    return;

                case Key.Up:
                    MoveVertical(-1);
                    e.Handled = true;
                    return;

                case Key.Down:
                    MoveVertical(1);
                    e.Handled = true;
                    return;

                case Key.Left:
                    MoveHorizontal(-1);
                    e.Handled = true;
                    return;

                case Key.Right:
                    MoveHorizontal(1);
                    e.Handled = true;
                    return;

                case Key.PageDown:
                    ScrollBy(ContentScrollViewer.ViewportHeight * 0.8);
                    e.Handled = true;
                    return;

                case Key.PageUp:
                    ScrollBy(-ContentScrollViewer.ViewportHeight * 0.8);
                    e.Handled = true;
                    return;
            }
        }

        public bool HandleControllerInput(ControllerInput button)
        {
            switch (button)
            {
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
                    ActivateFocused();
                    return true;

                case ControllerInput.B:
                    RaiseClosed();
                    return true;
            }

            return false;
        }

        private void ActivateFocused()
        {
            Button focused = Keyboard.FocusedElement as Button;

            if (focused == null || !IsAncestorOf(focused))
            {
                FocusPrimary();
                return;
            }

            if (ReferenceEquals(focused, CloseButton))
            {
                RaiseClosed();
            }
            else if (ReferenceEquals(focused, MyListButton))
            {
                RaiseMyListToggle();
            }
            else if (ReferenceEquals(focused, WatchNowButton))
            {
                RaiseWatchNow();
            }
        }

        // Vertical chain: Close <-> action row. Below the actions, Up/Down
        // scroll the page so a long synopsis/cast list can always be read
        // without a mouse.
        private void MoveVertical(int direction)
        {
            IInputElement focused = Keyboard.FocusedElement;
            bool onClose = ReferenceEquals(focused, CloseButton);
            bool onAction = ReferenceEquals(focused, WatchNowButton) ||
                            ReferenceEquals(focused, MyListButton);

            if (direction < 0)
            {
                if (onAction)
                {
                    if (ContentScrollViewer.VerticalOffset > 0.5)
                    {
                        ContentScrollViewer.ScrollToTop();
                    }

                    FocusElement(CloseButton);
                    return;
                }

                if (!onClose)
                {
                    FocusPrimary();
                    return;
                }

                return;
            }

            if (onClose)
            {
                FocusPrimary();
                return;
            }

            if (onAction)
            {
                // Nothing focusable below the actions: scroll instead.
                ScrollBy(ContentScrollViewer.ViewportHeight * 0.5);
                return;
            }

            FocusPrimary();
        }

        private void MoveHorizontal(int direction)
        {
            IInputElement focused = Keyboard.FocusedElement;

            if (ReferenceEquals(focused, WatchNowButton) && direction > 0)
            {
                FocusElement(MyListButton);
                return;
            }

            if (ReferenceEquals(focused, MyListButton) && direction < 0)
            {
                FocusElement(WatchNowButton);
                return;
            }

            if (!ReferenceEquals(focused, CloseButton) &&
                !ReferenceEquals(focused, WatchNowButton) &&
                !ReferenceEquals(focused, MyListButton))
            {
                FocusPrimary();
            }
        }

        private void ScrollBy(double delta)
        {
            double target = ContentScrollViewer.VerticalOffset + delta;
            ContentScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }
}
