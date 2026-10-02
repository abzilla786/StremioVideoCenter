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

        // Rebuilds the menu contents and returns a FocusGroup ready to be
        // pushed onto PlayerFocusController. Handles the "no tracks available"
        // case explicitly rather than showing an empty list.
        public FocusGroup Populate(
            string title,
            IReadOnlyList<MpvTrackInfo> tracks,
            int? selectedTrackId,
            bool includeOffOption,
            bool includeSubtitleDelay = false,
            double subtitleDelaySeconds = 0)
        {
            TitleText.Text = title;
            _delayValueText = null;

            StackPanel list = new StackPanel();
            FocusGroup group = new FocusGroup();

            if (includeOffOption)
            {
                Button offButton = BuildItemButton("Off", selectedTrackId == null);
                offButton.Click += (s, e) => RaiseTrackSelected(null);
                list.Children.Add(offButton);
                group.Add(offButton);

                if (selectedTrackId == null)
                    group.InitialElement = offButton;
            }

            if (tracks != null)
            {
                foreach (MpvTrackInfo track in tracks)
                {
                    MpvTrackInfo capturedTrack = track;
                    bool isSelected = selectedTrackId.HasValue && selectedTrackId.Value == capturedTrack.Id;

                    Button trackButton = BuildItemButton(capturedTrack.DisplayName, isSelected);
                    trackButton.Click += (s, e) => RaiseTrackSelected(capturedTrack.Id);
                    list.Children.Add(trackButton);
                    group.Add(trackButton);

                    if (isSelected)
                        group.InitialElement = trackButton;
                }
            }

            // The delay row is part of the Subtitles menu even when there
            // are no subtitle tracks, so it never makes the menu "empty".
            bool hasTrackItems = list.Children.Count > 0;

            if (includeSubtitleDelay)
            {
                AppendSubtitleDelayRow(list, group, subtitleDelaySeconds);
            }

            bool isEmpty = !hasTrackItems && !includeSubtitleDelay;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ItemsHost.Content = isEmpty ? null : list;

            return group;
        }

        // Live-updates the displayed delay while the menu is open (called when
        // mpv echoes a new sub-delay). No-op for the Audio menu.
        public void UpdateSubtitleDelay(double seconds)
        {
            if (_delayValueText != null)
                _delayValueText.Text = SubtitleDelay.Format(seconds);
        }

        private void AppendSubtitleDelayRow(StackPanel list, FocusGroup group, double delaySeconds)
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

            Button minus = BuildDelayButton("\u2212", "Subtitles earlier");
            minus.Click += (s, e) => RaiseDelayStep(-SubtitleDelayStepSeconds);

            Button plus = BuildDelayButton("+", "Subtitles later");
            plus.Click += (s, e) => RaiseDelayStep(SubtitleDelayStepSeconds);

            _delayValueText = new TextBlock
            {
                Text = SubtitleDelay.Format(delaySeconds),
                Foreground = Brushes.White,
                FontSize = 16,
                MinWidth = 70,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
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

            Button reset = BuildItemButton("Reset to 0.0s", false);
            reset.Content = "Reset to 0.0s";
            reset.HorizontalContentAlignment = HorizontalAlignment.Center;
            reset.Click += (s, e) =>
            {
                EventHandler handler = SubtitleDelayResetRequested;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            };
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
