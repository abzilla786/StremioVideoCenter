using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        public event EventHandler<SourceSelectedEventArgs> SourceSelected;
        public event EventHandler SelectionCancelled;

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
        // `sources` are unaffected. This never calls TMDB or any other
        // metadata source; it only reads properties already present on
        // the MediaItem it's given.

        public void Populate(List<NormalizedPlaybackSource> sources, MediaItem mediaItem = null)
        {
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

            SourceListPanel.Children.Clear();
            _sourceButtons.Clear();

            if (sources != null)
            {
                foreach (NormalizedPlaybackSource source in sources)
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
            if (!string.IsNullOrWhiteSpace(source.SourceAddonUrl))
            {
                TextBlock addonText = BuildInfoLine("Addon: " + source.SourceAddonUrl, 13, 0x77, 0x77, 0x77);
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

                case ControllerInput.A:
                    ActivateFocused();
                    return true;

                case ControllerInput.B:
                    RaiseSelectionCancelled();
                    return true;
            }

            return false;
        }

        // =========================================================
        // FOCUS MOVEMENT
        // =========================================================
        //
        // A simple single-column model: Up/Down cycles through the source
        // buttons only (wrapping first<->last), independent of the
        // always-visible, always-clickable Close button. Deliberately not
        // using PlayerFocusController - that class exists specifically for
        // MpvPlayer's multi-row control layout; a flat vertical list is a
        // simpler case that doesn't need that machinery.

        private void MoveFocus(int direction)
        {
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
                    index = _sourceButtons.Count - 1;
                }
                else if (index >= _sourceButtons.Count)
                {
                    index = 0;
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
