using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
    // Slice 21: renders as a vertically scrolling WrapPanel grid of
    // MediaCards (same pattern as MyListView/CatalogueBrowseView/
    // AnimeBrowseView), with the existing vertical DragScroll attached.
    // Reuses the same MediaCard - no new card UI. Selecting an entry
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

        // Same card-width column math as MyListView/CatalogueBrowseView
        // (190 card + 2 x 8 margin).
        private const double ResultsCardWidth = 206;

        private const int FocusRetryLimit = 20;

        private List<MediaItem> _items = new List<MediaItem>();

        public HistoryView()
        {
            InitializeComponent();

            // Vertical-only drag scrolling on the grid, following the
            // established grid-page pattern (see MyListView). DragScroll
            // only captures the mouse once the 8px drag threshold is
            // crossed and swallows the button release afterwards, so a
            // plain click still reaches MediaCard normally while a drag
            // does not raise MediaCard.SelectedEvent.
            DragScroll.Attach(PageScrollViewer, DragScrollAxis.Vertical);

            // MediaCard raises a bubbling SelectedEvent (click / Enter /
            // Space); same convention as the other grid pages.
            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(ResultCard_Selected));

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
            // Slice 16C fix (kept): determine the empty state from the
            // historyItems argument directly rather than reading back
            // the control's item count - at this point HistoryView has
            // not yet been attached to the visual tree (see
            // StremioVideoCenterView.ShowHistoryAsync, which calls
            // Initialize before HistoryHost.Children.Add), so the
            // just-set binding is not reliably reflected yet.
            _items = historyItems != null
                ? historyItems.ToList()
                : new List<MediaItem>();

            bool hasItems = _items.Count > 0;

            ResultsItemsControl.ItemsSource = _items;

            ResultsItemsControl.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        }

        // Slice 18B: re-reads the displayed items (e.g. after playback closes
        // while History is still open) so progress bars reflect the latest
        // Watch State, and restores focus to the first card.
        public void Refresh(IEnumerable<MediaItem> historyItems)
        {
            Initialize(historyItems);

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_items.Count > 0)
                    {
                        PageScrollViewer.ScrollToVerticalOffset(0);
                        FocusResultCard(0, 0);
                    }
                    else
                    {
                        CloseButton.Focus();
                    }
                }),
                DispatcherPriority.ContextIdle);
        }

        private void HistoryView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_items.Count > 0)
            {
                FocusResultCard(0, 0);
            }
            else
            {
                CloseButton.Focus();
            }
        }

        private void ResultCard_Selected(object sender, RoutedEventArgs e)
        {
            MediaCard card = e.OriginalSource as MediaCard;

            if (card == null)
            {
                return;
            }

            MediaItem mediaItem = card.DataContext as MediaItem;

            if (mediaItem == null)
            {
                return;
            }

            MediaSelected?.Invoke(this, new MediaItemSelectedEventArgs(mediaItem));

            e.Handled = true;
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
                MoveResultsGrid(-1, 0);
                return true;
            }

            if (button == ControllerInput.DPadRight)
            {
                MoveResultsGrid(1, 0);
                return true;
            }

            if (button == ControllerInput.DPadUp)
            {
                int current = GetFocusedResultIndex();

                // Up from the first row moves to Back; from Back, nothing.
                if (current >= 0 && current < GetResultsColumnsPerRow())
                {
                    CloseButton.Focus();
                    PageScrollViewer.ScrollToVerticalOffset(0);
                }
                else if (current >= 0)
                {
                    MoveResultsGrid(0, -1);
                }

                return true;
            }

            if (button == ControllerInput.DPadDown)
            {
                if (GetFocusedResultIndex() < 0)
                {
                    // From Back (or nothing focused): into the first card.
                    if (_items.Count > 0)
                    {
                        FocusResultCard(0, 0);
                    }
                }
                else
                {
                    MoveResultsGrid(0, 1);
                }

                return true;
            }

            return false;
        }

        // =========================================================
        // GRID FOCUS MOVEMENT
        // =========================================================
        //
        // Same shape as MyListView's GetResultsColumnsPerRow/
        // MoveResultsGrid/GetFocusedResultIndex/FocusResultCard: a plain
        // WrapPanel of fixed-width MediaCards, so it needs index math
        // plus ItemContainerGenerator-based container lookup.

        private int GetResultsColumnsPerRow()
        {
            double width = ResultsItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            return Math.Max(1, (int)(width / ResultsCardWidth));
        }

        private void MoveResultsGrid(int deltaColumns, int deltaRows)
        {
            int count = _items.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedResultIndex();

            if (current < 0)
            {
                FocusResultCard(0, 0);
                return;
            }

            int target = current + deltaColumns + (deltaRows * GetResultsColumnsPerRow());

            target = Math.Max(0, Math.Min(target, count - 1));

            FocusResultCard(target, 0);
        }

        private int GetFocusedResultIndex()
        {
            for (int i = 0; i < ResultsItemsControl.Items.Count; i++)
            {
                ContentPresenter presenter =
                    ResultsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

                if (presenter == null)
                {
                    continue;
                }

                MediaCard card = FindVisualChild<MediaCard>(presenter);

                if (card != null && card.IsKeyboardFocusWithin)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusResultCard(int index, int attempt)
        {
            if (index < 0 || index >= ResultsItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                ResultsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                // Container not generated yet (layout pending); retry a
                // bounded number of times rather than looping forever.
                if (attempt < FocusRetryLimit)
                {
                    Dispatcher.BeginInvoke(
                        new Action(() => FocusResultCard(index, attempt + 1)),
                        DispatcherPriority.Loaded);
                }

                return;
            }

            MediaCard card = FindVisualChild<MediaCard>(presenter);

            if (card == null)
            {
                return;
            }

            card.Focus();
            card.BringIntoView();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                T result = child as T;

                if (result != null)
                {
                    return result;
                }

                result = FindVisualChild<T>(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
