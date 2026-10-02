using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StremioVideoCenterControls.Player.Menus
{
    public sealed class TrackMenuItemSelectedEventArgs : EventArgs
    {
        public int? TrackId { get; private set; }

        public TrackMenuItemSelectedEventArgs(int? trackId)
        {
            TrackId = trackId;
        }
    }

    // Shared by the Audio and Subtitle buttons - both are "pick one track from
    // a list of MpvTrackInfo, with an optional OFF entry". Reused rather than
    // duplicated because the two menus only differ in title/whether OFF is shown.
    public partial class TrackSelectionMenu : UserControl
    {
        // Subtitle-delay step in seconds, used by the Subtitles menu's
        // "-" / "+" buttons.
        public const double SubtitleDelayStepSeconds = SubtitleDelay.StepSeconds;

        public event EventHandler<TrackMenuItemSelectedEventArgs> TrackSelected;

        // Raised (menu stays open) when the user presses the delay "-" or
        // "+" button; the argument is the signed step in seconds.
        public event EventHandler<double> SubtitleDelayStepRequested;

        // Raised (menu stays open) when the user presses "Reset".
        public event EventHandler SubtitleDelayResetRequested;

        private TextBlock _delayValueText;

        public TrackSelectionMenu()
        {
            InitializeComponent();
        }

        // Rebuilds the menu from mpv's current state and returns the
        // FocusGroup for PlayerFocusController (to push, or to swap in for a
        // live refresh). WHAT the menu contains and its focus order come from
        // TrackMenuLayout (pure); this method only renders it:
        //   - loading: "Loading tracks..." (never the previous file's tracks);
        //   - loaded but none: a plain "no tracks" line, not an error;
        //   - otherwise one button per track, the selected one marked, plus
        //     (Subtitles) Off and the delay controls.
        public FocusGroup Populate(
            string title,
            TrackMenuLayout layout)
        {
            TitleText.Text = title;
            _delayValueText = null;

            StackPanel list = new StackPanel();
            FocusGroup group = new FocusGroup();

            // Items in layout order; the delay value (not an item, never
            // focusable) sits between the "-" and "+" buttons.
            Button minus = null;
            Button plus = null;
            Button reset = null;

            foreach (TrackMenuItem item in layout.Items)
            {
                switch (item.Kind)
                {
                    case TrackMenuItemKind.Off:
                    case TrackMenuItemKind.Track:
                    {
                        Button button = BuildItemButton(item.Label, item.IsSelected);
                        int? trackId = item.TrackId;
                        button.Click += (s, e) => RaiseTrackSelected(trackId);
                        list.Children.Add(button);
                        group.Add(button);
                        if (item.IsSelected && group.InitialElement == null)
                            group.InitialElement = button;
                        break;
                    }

                    case TrackMenuItemKind.DelayDecrease:
                        minus = BuildDelayButton("\u2212", "Subtitles earlier");
                        minus.Click += (s, e) => RaiseDelayStep(-SubtitleDelayStepSeconds);
                        break;

                    case TrackMenuItemKind.DelayIncrease:
                        plus = BuildDelayButton("+", "Subtitles later");
                        plus.Click += (s, e) => RaiseDelayStep(SubtitleDelayStepSeconds);
                        break;

                    case TrackMenuItemKind.DelayReset:
                        reset = BuildItemButton("Reset to 0.0s", false);
                        reset.HorizontalContentAlignment = HorizontalAlignment.Center;
                        reset.Click += (s, e) =>
                        {
                            EventHandler handler = SubtitleDelayResetRequested;
                            if (handler != null)
                                handler(this, EventArgs.Empty);
                        };
                        break;
                }
            }

            if (layout.HasDelayRow && minus != null && plus != null && reset != null)
            {
                AppendSubtitleDelayRow(list, group, layout.DelaySeconds, minus, plus, reset);
            }

            EmptyStateText.Text = layout.StatusMessage ?? string.Empty;
            EmptyStateText.Visibility = layout.StatusMessage != null ? Visibility.Visible : Visibility.Collapsed;
            ItemsHost.Content = list.Children.Count > 0 ? list : null;

            return group;
        }

        // Live-updates the displayed delay while the menu is open (called when
        // mpv echoes a new sub-delay). No-op for the Audio menu.
        public void UpdateSubtitleDelay(double seconds)
        {
            if (_delayValueText != null)
                _delayValueText.Text = SubtitleDelay.Format(seconds);
        }

        private void AppendSubtitleDelayRow(
            StackPanel list,
            FocusGroup group,
            double delaySeconds,
            Button minus,
            Button plus,
            Button reset)
        {
            list.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(8, 8, 8, 6),
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            });

            list.Children.Add(new TextBlock
            {
                Text = "Subtitle Delay",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 2, 10, 4)
            });

            // Display only: no action, not focusable.
            _delayValueText = new TextBlock
            {
                Text = SubtitleDelay.Format(delaySeconds),
                Foreground = Brushes.White,
                FontSize = 16,
                MinWidth = 70,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                IsHitTestVisible = false
            };

            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            row.Children.Add(minus);
            row.Children.Add(_delayValueText);
            row.Children.Add(plus);
            list.Children.Add(row);
            list.Children.Add(reset);

            group.Add(minus);
            group.Add(plus);
            group.Add(reset);
        }

        private void RaiseDelayStep(double deltaSeconds)
        {
            EventHandler<double> handler = SubtitleDelayStepRequested;
            if (handler != null)
                handler(this, deltaSeconds);
        }

        private Button BuildDelayButton(string text, string toolTip)
        {
            return new Button
            {
                Style = (Style)Resources["MenuItemButtonStyle"],
                Content = text,
                ToolTip = toolTip,
                Width = 52,
                FontSize = 18,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            };
        }

        private void RaiseTrackSelected(int? trackId)
        {
            EventHandler<TrackMenuItemSelectedEventArgs> handler = TrackSelected;
            if (handler != null)
                handler(this, new TrackMenuItemSelectedEventArgs(trackId));
        }

        private Button BuildItemButton(string text, bool isSelected)
        {
            return new Button
            {
                Style = (Style)Resources["MenuItemButtonStyle"],
                Content = (isSelected ? "\u25CF " : "\u25CB ") + text,
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x99, 0x33, 0x88, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            };
        }
    }
}
