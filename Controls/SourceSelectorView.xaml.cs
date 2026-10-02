using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // Carries the user's chosen source. Source is never null when this is
    // raised - SourceSelected only ever fires from a button built for a
    // real, non-null NormalizedPlaybackSource (see BuildSourceButton).
    public sealed class SourceSelectedEventArgs : EventArgs
    {
        public NormalizedPlaybackSource Source { get; private set; }

        public SourceSelectedEventArgs(NormalizedPlaybackSource source)
        {
            Source = source;
        }
    }

    // Self-contained "pick one source" UI. Receives already-normalized data
    // via Populate() and emits the user's choice via SourceSelected/
    // SelectionCancelled - nothing in this class calls Stremio, Real-Debrid,
    // the aggregator, the normalizer, or MPV. Wired into the live playback
    // path as of Slice 9 (StremioVideoCenterView routes controller input to
    // this control via HandleControllerInput while a selection is active,
    // and bridges SourceSelected/SelectionCancelled into an awaitable
    // result via TaskCompletionSource in PlaySelectedMediaAsync).
    //
    // Grid+ScrollViewer overlay, not a Popup: the airspace problem that
    // required MpvPlayer's own Popup (rendering above a native embedded
    // HWND) doesn't apply here - this shows either before MpvPlayer exists
    // or after a previous instance has already been torn down.
    //
    // Hand-rolled Button list, not a ListBox: matches the same choice
    // TrackSelectionMenu (inside MpvPlayer) already made for the same
    // reason - a ListBox's own internal keyboard handling risks colliding
    // with a custom controller-input dispatch model.
    //
    // Slice 10: visual polish only - stronger header hierarchy, an
    // optional media-context line, larger/more readable source cards with
    // chip-style metadata badges instead of a single joined text line, and
    // a clearer focused-card indicator (left accent bar). No architecture,
    // event, or navigation behaviour changed.
    public partial class SourceSelectorView : UserControl
    {
        private readonly List<Button> _sourceButtons = new List<Button>();

        // Slice 17B: the MediaItem currently on display, kept only so the
        // header's My List button can be re-focused/re-styled after a
        // toggle. This control never reads MyListService or a profile
        // itself (see the class remarks above on staying self-contained) -
        // it only remembers what Populate() was given and tells the caller
        // "the My List button was pressed" via MyListToggleRequested. The
        // caller (which owns MyListService and the active profile) decides
        // what that means and reports the resulting state back via
        // SetMyListState.
        private MediaItem _currentMediaItem;

        public event EventHandler<SourceSelectedEventArgs> SourceSelected;
        public event EventHandler SelectionCancelled;
        public event EventHandler MyListToggleRequested;

        public SourceSelectorView()
        {
            InitializeComponent();
        }

        // =========================================================
        // POPULATE
        // =========================================================
        //
        // Displays sources in exactly the order supplied - no sorting,
        // ranking, deduplication, or filtering beyond skipping literal
        // null entries (which would otherwise produce an unusable blank
        // button). Ordering/deduplication are the aggregator's job, not
        // this control's.
        //
        // mediaItem is optional (Slice 10) and used only to populate the
        // header's media-context line (title/year/type/rating) when the
        // caller has one available - existing callers passing only
        // `sources` are unaffected. This never calls any
        // metadata source; it only reads properties already present on
        // the MediaItem it's given.

        public void Populate(List<NormalizedPlaybackSource> sources, MediaItem mediaItem = null)
        {
            _currentMediaItem = mediaItem;

            PopulateHeader(mediaItem);

            SourceListPanel.Children.Clear();
            _sourceButtons.Clear();

            List<NormalizedPlaybackSource> orderedSources = SortByDescendingQuality(sources);

            if (orderedSources != null)
            {
                foreach (NormalizedPlaybackSource source in orderedSources)
                {
                    if (source == null)
                    {
                        continue;
                    }

                    Button button = BuildSourceButton(source);
                    SourceListPanel.Children.Add(button);
                    _sourceButtons.Add(button);
                }
            }

            if (_sourceButtons.Count == 0)
            {
                ShowEmptyState();
            }
            else
            {
                ShowSourceList();
            }
        }

        // =========================================================
        // QUALITY ORDERING (Slice 14 correction)
        // =========================================================
        //
        // Presentation-only display ordering: sources are shown in
        // descending video quality using ResolutionHeight, which
        // PlaybackSourceNormalizationService already parses from every
        // source's Name/Title/Description (see that service's
        // KnownResolutionTokens/KnownDelimitedResolutionTokens) - no new
        // quality parser is introduced here. A source with no recognisable
        // quality (ResolutionHeight == null) sorts after every
        // known-quality source rather than being dropped or rejected.
        //
        // LINQ's OrderByDescending is a documented stable sort, so two
        // sources with the same quality (or both unknown) keep their
        // original relative order exactly as the aggregator/discovery
        // pipeline produced it - this deliberately introduces no
        // provider/addon ranking, no RD-vs-direct preference, and no
        // codec/HDR/audio/file-size/seed weighting. Those remain future
        // work (see PlaybackSourceAggregationService's own remarks on not
        // picking a "best" source). This is purely how the existing,
        // unreordered source list is displayed - the underlying `sources`
        // list passed in is never mutated.
        private static List<NormalizedPlaybackSource> SortByDescendingQuality(
            List<NormalizedPlaybackSource> sources)
        {
            if (sources == null)
            {
                return null;
            }

            return sources
                .OrderByDescending(source =>
                    source != null && source.ResolutionHeight.HasValue
                        ? source.ResolutionHeight.Value
                        : -1)
                .ToList();
        }

        // =========================================================
        // HEADER (media info line, or the richer episode context panel)
        // =========================================================
        //
        // Slice 14 (Complete TV Experience): an episode-shaped MediaItem
        // (Season and Episode both populated - the same test
        // PlaySelectedMediaAsync/EpisodeBrowserView.BuildEpisodeMediaItem
        // already use elsewhere) gets EpisodeContextPanel instead of the
        // original single MediaInfoText line - thumbnail, SxxExx, title,
        // rating/air date, and overview, all read from fields
        // EpisodeBrowserView already populated on the MediaItem (no new
        // network request). A movie or null MediaItem keeps the original
        // single-line header, completely unchanged.
        private void PopulateHeader(MediaItem mediaItem)
        {
            bool isEpisode = mediaItem != null &&
                mediaItem.Season.HasValue &&
                mediaItem.Episode.HasValue;

            if (!isEpisode)
            {
                EpisodeContextPanel.Visibility = Visibility.Collapsed;
                EpisodeThumbnailImage.Source = null;

                // My List is series/movie-level: shown for movies only. An
                // episode-shaped item is handled by the Episode Browser.
                MyListButton.Visibility = Visibility.Visible;

                if (mediaItem != null && !string.IsNullOrWhiteSpace(mediaItem.Overview))
                {
                    MovieSynopsisText.Text = mediaItem.Overview;
                    MovieSynopsisText.Visibility = Visibility.Visible;
                }
                else
                {
                    MovieSynopsisText.Visibility = Visibility.Collapsed;
                }

                string mediaInfo = BuildMediaInfoLine(mediaItem);

                if (!string.IsNullOrWhiteSpace(mediaInfo))
                {
                    MediaInfoText.Text = mediaInfo;
                    MediaInfoText.Visibility = Visibility.Visible;
                }
                else
                {
                    MediaInfoText.Visibility = Visibility.Collapsed;
                }

                return;
            }

            MediaInfoText.Visibility = Visibility.Collapsed;
            MovieSynopsisText.Visibility = Visibility.Collapsed;
            MyListButton.Visibility = Visibility.Collapsed;
            EpisodeContextPanel.Visibility = Visibility.Visible;

            EpisodeCodeText.Text =
                "S" + mediaItem.Season.Value.ToString().PadLeft(2, '0') +
                "E" + mediaItem.Episode.Value.ToString().PadLeft(2, '0');

            EpisodeTitleText.Text =
                !string.IsNullOrWhiteSpace(mediaItem.Title) ? mediaItem.Title : "Episode";

            List<string> metaParts = new List<string>();

            if (!string.IsNullOrWhiteSpace(mediaItem.Rating))
            {
                metaParts.Add("★ " + mediaItem.Rating);
            }

            if (!string.IsNullOrWhiteSpace(mediaItem.ReleaseDate))
            {
                metaParts.Add(mediaItem.ReleaseDate);
            }
            else if (!string.IsNullOrWhiteSpace(mediaItem.Year))
            {
                metaParts.Add(mediaItem.Year);
            }

            if (metaParts.Count > 0)
            {
                EpisodeMetaText.Text = string.Join("   ·   ", metaParts);
                EpisodeMetaText.Visibility = Visibility.Visible;
            }
            else
            {
                EpisodeMetaText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(mediaItem.Overview))
            {
                EpisodeOverviewText.Text = mediaItem.Overview;
                EpisodeOverviewText.Visibility = Visibility.Visible;
            }
            else
            {
                EpisodeOverviewText.Visibility = Visibility.Collapsed;
            }

            EpisodeThumbnailImage.Source = null;

            if (!string.IsNullOrWhiteSpace(mediaItem.PosterPath))
            {
                BitmapImage bitmap = TryCreateBitmapImage(mediaItem.PosterPath);

                if (bitmap != null)
                {
                    EpisodeThumbnailImage.Source = bitmap;
                }
            }
        }

        // WPF downloads a BitmapImage with a remote UriSource
        // asynchronously and simply renders nothing if the download
        // fails - no "broken image" glyph is ever shown natively, so a
        // malformed URL (caught here) or a failed download (left to WPF)
        // both degrade to the fallback glyph behind it. Never throws.
        // Duplicated from EpisodeBrowserView rather than shared, matching
        // this project's existing convention (see CinemetaService's own
        // remarks on duplicating DownloadStringAsync) of not introducing
        // shared infrastructure for a same-slice need.
        private static BitmapImage TryCreateBitmapImage(string url)
        {
            try
            {
                Uri uri;

                if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                {
                    return null;
                }

                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnDemand;
                bitmap.EndInit();

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // Only uses fields already on MediaItem - never fetches anything,
        // never invents a value. Any missing piece is simply omitted from
        // the line rather than shown as a placeholder.
        private static string BuildMediaInfoLine(MediaItem mediaItem)
        {
            if (mediaItem == null)
            {
                return null;
            }

            List<string> parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(mediaItem.Title))
            {
                parts.Add(mediaItem.Title);
            }

            if (!string.IsNullOrWhiteSpace(mediaItem.Year))
            {
                parts.Add(mediaItem.Year);
            }

            if (!string.IsNullOrWhiteSpace(mediaItem.Type))
            {
                parts.Add(mediaItem.Type);
            }

            if (!string.IsNullOrWhiteSpace(mediaItem.Rating))
            {
                parts.Add("\u2605 " + mediaItem.Rating);
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return string.Join("   \u00B7   ", parts);
        }

        // =========================================================
        // MY LIST (Slice 17B)
        // =========================================================
        //
        // Reflects a state the caller already determined (via MyListService
        // and the active profile) - this control never queries or persists
        // anything itself. Safe to call any time after Populate(); called
        // once with the initial state right after Populate(), and again
        // after MyListToggleRequested has been handled, so the button
        // always shows the current state immediately.
        public void SetMyListState(bool isInMyList)
        {
            MyListButton.Content = isInMyList ? "✓ IN MY LIST" : "+ MY LIST";
            MyListButton.ToolTip = isInMyList ? "Remove from My List" : "Add to My List";
        }

        private void MyListButton_Click(object sender, RoutedEventArgs e)
        {
            EventHandler handler = MyListToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ShowEmptyState()
        {
            SourceListScrollViewer.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;

            // Deferred, matching MpvPlayer's own established pattern for
            // focusing something that may not yet be part of a rendered,
            // focusable visual tree the moment Populate() is called.
            Dispatcher.BeginInvoke(
                new Action(() => FocusElement(EmptyStateBackButton)),
                DispatcherPriority.Input);
        }

        private void ShowSourceList()
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            SourceListScrollViewer.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(
                new Action(() => FocusElement(_sourceButtons[0])),
                DispatcherPriority.Input);
        }

        // =========================================================
        // BUILD SOURCE BUTTON
        // =========================================================
        //
        // Only ever reads fields already present on NormalizedPlaybackSource
        // - never invents a value for a null/unknown field. Every optional
        // line/chip is added only when the corresponding field is actually
        // present, so the display stays readable (no "Unknown" clutter)
        // when metadata is missing.

        private Button BuildSourceButton(NormalizedPlaybackSource source)
        {
            StackPanel content = new StackPanel();

            string primaryLabel =
                !string.IsNullOrWhiteSpace(source.Name) ? source.Name :
                !string.IsNullOrWhiteSpace(source.Title) ? source.Title :
                "Source";

            // ---- Title row: primary label + Kind badge ----
            Grid titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock primaryText = new TextBlock
            {
                Text = primaryLabel,
                Foreground = Brushes.White,
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(primaryText, 0);
            titleRow.Children.Add(primaryText);

            bool isRealDebrid = source.Kind == PlaybackSourceKind.RealDebrid;

            Border kindBadge = new Border
            {
                Background = new SolidColorBrush(isRealDebrid
                    ? Color.FromArgb(0xAA, 0x8E, 0x24, 0xAA)
                    : Color.FromArgb(0xAA, 0x2E, 0x7D, 0x32)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            kindBadge.Child = new TextBlock
            {
                Text = isRealDebrid ? "REAL-DEBRID" : "DIRECT",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            };

            Grid.SetColumn(kindBadge, 1);
            titleRow.Children.Add(kindBadge);

            content.Children.Add(titleRow);

            // ---- Release name (only if present and distinct from the
            // primary label - avoids showing the same text twice) ----
            if (!string.IsNullOrWhiteSpace(source.ReleaseName) &&
                !string.Equals(source.ReleaseName, primaryLabel, StringComparison.OrdinalIgnoreCase))
            {
                content.Children.Add(BuildInfoLine(source.ReleaseName, 15, 0xAA, 0xAA, 0xAA));
            }

            // ---- Description (only if present) ----
            if (!string.IsNullOrWhiteSpace(source.Description))
            {
                content.Children.Add(BuildInfoLine(source.Description, 14, 0x99, 0x99, 0x99));
            }

            // ---- Metadata chips (only known values, nothing invented) ----
            UIElement chipsPanel = BuildMetadataChipsPanel(source);
            if (chipsPanel != null)
            {
                content.Children.Add(chipsPanel);
            }

            // ---- Addon origin (only if present) ----
            // Shows the originating addon's friendly display name
            // (AddonInstance.FriendlyName, e.g. "Torrentio RD") rather than
            // its manifest URL. For a Real-Debrid-configured addon, that
            // URL carries the user's RD key - it must never reach this UI.
            // A safe generic fallback is used if no friendly name is
            // available; the URL itself is never shown, by design.
            if (!string.IsNullOrWhiteSpace(source.SourceAddonUrl))
            {
                string addonDisplayName =
                    !string.IsNullOrWhiteSpace(source.SourceAddonName)
                        ? source.SourceAddonName
                        : "Unknown addon";

                TextBlock addonText = BuildInfoLine("Addon: " + addonDisplayName, 13, 0x77, 0x77, 0x77);
                addonText.Margin = new Thickness(0, 8, 0, 0);
                content.Children.Add(addonText);
            }

            Button button = new Button
            {
                Style = (Style)Resources["SourceItemButtonStyle"],
                Content = content,
                Tag = source
            };

            button.Click += SourceButton_Click;

            return button;
        }

        private static TextBlock BuildInfoLine(string text, double fontSize, byte r, byte g, byte b)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        // Builds a wrapping row of small pill-shaped metadata chips
        // (quality, HDR, codec, container, audio, Atmos, file size,
        // subtitle languages) - only for fields that are actually present.
        // Returns null when there is nothing to show, so callers can skip
        // adding an empty row.
        private static UIElement BuildMetadataChipsPanel(NormalizedPlaybackSource source)
        {
            List<string> tags = CollectMetadataTags(source);

            if (tags.Count == 0)
            {
                return null;
            }

            WrapPanel panel = new WrapPanel
            {
                Margin = new Thickness(0, 10, 0, 0)
            };

            foreach (string tag in tags)
            {
                panel.Children.Add(BuildChip(tag));
            }

            return panel;
        }

        private static Border BuildChip(string text)
        {
            Border chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xE9, 0x1E, 0x63)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xE9, 0x1E, 0x63)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 6)
            };

            chip.Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0xCB)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };

            return chip;
        }

        // Only known values are included - a field that's null/unknown is
        // simply omitted, never shown as a placeholder. Same field set and
        // same collection logic as before Slice 10; only the output shape
        // changed (a list of individual chips instead of one joined line).
        private static List<string> CollectMetadataTags(NormalizedPlaybackSource source)
        {
            List<string> tags = new List<string>();

            if (source.ResolutionHeight.HasValue)
            {
                tags.Add(source.ResolutionHeight.Value + "p");
            }

            if (!string.IsNullOrWhiteSpace(source.HdrInfo))
            {
                tags.Add(source.HdrInfo);
            }

            if (!string.IsNullOrWhiteSpace(source.CodecInfo))
            {
                tags.Add(source.CodecInfo);
            }

            if (!string.IsNullOrWhiteSpace(source.ContainerInfo))
            {
                tags.Add(source.ContainerInfo);
            }

            if (!string.IsNullOrWhiteSpace(source.AudioInfo))
            {
                tags.Add(source.AudioInfo);
            }

            if (!string.IsNullOrWhiteSpace(source.AtmosInfo))
            {
                tags.Add(source.AtmosInfo);
            }

            if (source.FileSizeBytes.HasValue)
            {
                tags.Add(FormatFileSize(source.FileSizeBytes.Value));
            }

            if (source.SubtitleLanguages != null && source.SubtitleLanguages.Count > 0)
            {
                tags.Add("CC: " + string.Join(", ", source.SubtitleLanguages));
            }

            return tags;
        }

        private static string FormatFileSize(long bytes)
        {
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;

            if (gb >= 0.1)
            {
                return gb.ToString("0.0") + " GB";
            }

            double mb = bytes / 1024.0 / 1024.0;
            return mb.ToString("0") + " MB";
        }

        // =========================================================
        // ACTIVATION - mouse click and keyboard/controller activation
        // both end up here. Keyboard/controller activation (see
        // ActivateFocused) synthesizes this exact Click event on the
        // focused button rather than calling a separate "select" method,
        // so there is provably only one selection path, not two that
        // happen to agree.
        // =========================================================

        private void SourceButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            NormalizedPlaybackSource source = button.Tag as NormalizedPlaybackSource;
            if (source == null)
            {
                return;
            }

            EventHandler<SourceSelectedEventArgs> handler = SourceSelected;
            if (handler != null)
            {
                handler(this, new SourceSelectedEventArgs(source));
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseSelectionCancelled();
        }

        private void RaiseSelectionCancelled()
        {
            EventHandler handler = SelectionCancelled;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void SourceSelectorView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                    MoveFocus(-1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    MoveFocus(1);
                    e.Handled = true;
                    break;

                case Key.Left:
                    if (MoveHeaderFocus(-1))
                    {
                        e.Handled = true;
                    }
                    break;

                case Key.Right:
                    if (MoveHeaderFocus(1))
                    {
                        e.Handled = true;
                    }
                    break;

                case Key.Enter:
                    ActivateFocused();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    RaiseSelectionCancelled();
                    e.Handled = true;
                    break;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================
        //
        // Mirrors the existing project convention (MpvPlayer.
        // HandleControllerInput's exact signature/shape). Called by
        // StremioVideoCenterView.HandleControllerInput while a selection
        // is active (wired in Slice 9).

        public bool HandleControllerInput(ControllerInput button)
        {
            switch (button)
            {
                case ControllerInput.DPadUp:
                    MoveFocus(-1);
                    return true;

                case ControllerInput.DPadDown:
                    MoveFocus(1);
                    return true;

                case ControllerInput.DPadLeft:
                    return MoveHeaderFocus(-1);

                case ControllerInput.DPadRight:
                    return MoveHeaderFocus(1);

                case ControllerInput.A:
                    ActivateFocused();
                    return true;

                case ControllerInput.B:
                    RaiseSelectionCancelled();
                    return true;
            }

            return false;
        }

        // Left/Right moves only between the two header buttons (My List
        // and Close) - every other control in this view is part of the
        // single vertical source list (see MoveFocus), which has no
        // horizontal neighbours, so this is a no-op (returns false) unless
        // one of the header buttons is actually focused.
        private bool MoveHeaderFocus(int direction)
        {
            if (MyListButton.IsKeyboardFocused && direction > 0)
            {
                FocusElement(CloseButton);
                return true;
            }

            if (CloseButton.IsKeyboardFocused && direction < 0 &&
                MyListButton.Visibility == Visibility.Visible)
            {
                FocusElement(MyListButton);
                return true;
            }

            return false;
        }

        // =========================================================
        // FOCUS MOVEMENT
        // =========================================================
        //
        // A simple single-column model: Up/Down moves through the source
        // buttons, with the always-visible, always-clickable Close button
        // treated as one more stop at the top of that same chain rather
        // than a separate control - Up from the topmost source reaches
        // Close, and Down from Close reaches the first source, instead
        // of the previous cyclic wrap-around (Up from the top used to
        // skip straight to the bottom, and vice versa). Deliberately not
        // using PlayerFocusController - that class exists specifically for
        // MpvPlayer's multi-row control layout; a flat vertical list is a
        // simpler case that doesn't need that machinery.

        private void MoveFocus(int direction)
        {
            if (CloseButton.IsKeyboardFocused)
            {
                if (direction > 0 && _sourceButtons.Count > 0)
                {
                    FocusElement(_sourceButtons[0]);
                }

                // Up while already on Close is a no-op - there is
                // nothing above it to move to.
                return;
            }

            if (_sourceButtons.Count == 0)
            {
                return;
            }

            Button current = Keyboard.FocusedElement as Button;
            int index = _sourceButtons.IndexOf(current);

            if (index < 0)
            {
                index = 0;
            }
            else
            {
                index += direction;

                if (index < 0)
                {
                    // Up from the topmost source reaches the
                    // always-visible Close control instead of wrapping
                    // to the bottom of the list.
                    FocusElement(CloseButton);
                    return;
                }

                if (index >= _sourceButtons.Count)
                {
                    // Down from the bottom-most source stays put -
                    // there is no further target below the list, so no
                    // wrap-around happens.
                    return;
                }
            }

            FocusElement(_sourceButtons[index]);
        }

        private void ActivateFocused()
        {
            Button focused = Keyboard.FocusedElement as Button;

            if (focused != null)
            {
                focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        // Focuses the element and explicitly scrolls it into view - relying
        // solely on WPF's default RequestBringIntoView bubbling would
        // usually also work, but calling BringIntoView() directly makes
        // "keep the focused item visible in the ScrollViewer" deterministic
        // rather than incidental.
        private static void FocusElement(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            element.Focus();
            Keyboard.Focus(element);
            element.BringIntoView();
        }
    }
}
