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
        public event EventHandler<TrackMenuItemSelectedEventArgs> TrackSelected;

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
            bool includeOffOption)
        {
            TitleText.Text = title;

            StackPanel list = new StackPanel();
            FocusGroup group = new FocusGroup();

            if (includeOffOption)
            {
                Button offButton = BuildItemButton("Off", selectedTrackId == null);
                offButton.Click += (s, e) => RaiseTrackSelected(null);
                list.Children.Add(offButton);
                group.Add(offButton);
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
                }
            }

            bool isEmpty = list.Children.Count == 0;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ItemsHost.Content = isEmpty ? null : list;

            return group;
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
