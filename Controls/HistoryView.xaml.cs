using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace StremioVideoCenterControls
{
    // =============================================================
    // HISTORY VIEW (Slice 16C - Continue Watching + History)
    // =============================================================
    //
    // Profile-owned "what you've watched or visited" page, read-only
    // display of WatchHistoryService's entries for the active profile -
    // this view never touches WatchHistoryService directly (see
    // StremioVideoCenterView.ShowHistoryAsync, which builds the
    // display MediaItems and calls Initialize below), matching the
    // existing convention of keeping data-access out of the view layer.
    //
    // Reuses the exact same MediaSection/MediaCard rendering path Home
    // already uses - no new card UI, no new grid. Selecting an entry
    // raises MediaSelected exactly like every other overlay
    // (EpisodeBrowserView/CatalogueBrowseView/LocalMediaView), so it
    // flows through StremioVideoCenterView's existing
    // HandleMediaSelectedAsync routing unchanged: a movie-shaped entry
    // (no Season/Episode) plays directly, a TV/Kitsu series-shaped entry
    // (StremioId only, no Season/Episode) opens the Episode Browser -
    // never a next-episode inference, matching the Slice 16C boundary
    // against Slice 18 resume/autoplay behaviour.
    //
    // Same overlay architecture as LocalMediaView/EpisodeBrowserView: a
    // UserControl added into its own host Grid, Closed/MediaSelected
    // bridged into an awaitable Task by the caller, and the same
    // PreviewKeyDown/HandleControllerInput(ControllerInput) shape so it
    // participates in StremioVideoCenterView's existing "most specific
    // overlay wins" input-priority chain unchanged.
    public partial class HistoryView : UserControl
    {
        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        public HistoryView()
        {
            InitializeComponent();

            HistorySection.MediaSelected += HistorySection_MediaSelected;

            Loaded += HistoryView_Loaded;
        }

        // =========================================================
        // INITIALIZE
        // =========================================================
        //
        // historyItems are already-built display MediaItems (see
        // StremioVideoCenterView.ShowHistoryAsync) - this view never
        // reconstructs them itself, mirroring how LocalMediaView.
        // Initialize is handed already-known facts rather than
        // computing them.
        public void Initialize(IEnumerable<MediaItem> historyItems)
        {
            // Slice 16C fix: determine the empty state from the
            // historyItems argument directly rather than reading back
            // HistorySection.HasItems immediately after assigning
            // HistorySection.Items - at this point HistoryView has not
            // yet been attached to the visual tree (see
            // StremioVideoCenterView.ShowHistoryAsync, which calls
            // Initialize before HistoryHost.Children.Add), and
            // HasItems (MediaItemsControl.Items.Count) was found to not
            // reliably reflect the just-set Items binding yet at that
            // point, causing History to always show its empty state
            // even when non-empty entries were passed in.
            bool hasItems = historyItems != null && historyItems.Any();

            HistorySection.Items = historyItems;

            HistorySection.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HistoryView_Loaded(object sender, RoutedEventArgs e)
        {
            if (HistorySection.HasItems)
            {
                HistorySection.FocusFirstCard();
            }
            else
            {
                CloseButton.Focus();
            }
        }

        private void HistorySection_MediaSelected(object sender, MediaItemSelectedEventArgs e)
        {
            MediaSelected?.Invoke(this, e);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            EventHandler handler = Closed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void HistoryView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RaiseClosed();
                e.Handled = true;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            if (button == ControllerInput.B)
            {
                RaiseClosed();
                return true;
            }

            if (button == ControllerInput.A)
            {
                if (Keyboard.FocusedElement is MediaCard focusedCard)
                {
                    focusedCard.RaiseEvent(new RoutedEventArgs(MediaCard.SelectedEvent));
                    return true;
                }

                if (Keyboard.FocusedElement is ButtonBase focusedButton)
                {
                    focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    return true;
                }
            }

            if (button == ControllerInput.DPadLeft)
            {
                HistorySection.MoveHorizontal(-1);
                return true;
            }

            if (button == ControllerInput.DPadRight)
            {
                HistorySection.MoveHorizontal(1);
                return true;
            }

            return false;
        }
    }
}
