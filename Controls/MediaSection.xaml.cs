using StremioVideoCenter.Models;
using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenterControls
{
    public partial class MediaSection : UserControl
    {
        public MediaSection()
        {
            InitializeComponent();

            AddHandler(
                MediaCard.SelectedEvent,
                new RoutedEventHandler(MediaCard_Selected));
        }

        // =========================================================
        // MEDIA SELECTION
        // =========================================================

        public event EventHandler<MediaItemSelectedEventArgs>
            MediaSelected;

        private void MediaCard_Selected(
            object sender,
            RoutedEventArgs e)
        {
            MediaCard card =
                e.OriginalSource as MediaCard;

            if (card == null)
            {
                return;
            }

            MediaItem mediaItem =
                card.DataContext as MediaItem;

            if (mediaItem == null)
            {
                return;
            }

            MediaSelected?.Invoke(
                this,
                new MediaItemSelectedEventArgs(
                    mediaItem));

            e.Handled = true;
        }

        // =========================================================
        // TITLE
        // =========================================================

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(MediaSection),
                new PropertyMetadata(string.Empty));

        // =========================================================
        // ITEMS
        // =========================================================

        public IEnumerable Items
        {
            get => (IEnumerable)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(
                nameof(Items),
                typeof(IEnumerable),
                typeof(MediaSection),
                new PropertyMetadata(null));

        // Lifecycle fix: whether this section actually has at least one
        // realized item to focus. Reads the real ItemsControl (the same
        // source FocusCard/FocusLastCard/MoveHorizontal already use),
        // not the bound Items source itself - a bound ObservableCollection
        // is non-null even while genuinely empty (e.g. MyList/
        // ContinueWatching before Slice 16/17 ever populate them), so
        // callers that only checked "Items != null" (FocusFirstAvailable
        // Section) could select a section with nothing in it to focus.
        public bool HasItems
        {
            get
            {
                return MediaItemsControl.Items.Count > 0;
            }
        }

        // =========================================================
        // "MORE" LINK (Real Catalogue & Content Foundation)
        // =========================================================
        //
        // Opt-in per instance. MoreButtonVisibility defaults to
        // Collapsed, so a MediaSection that never sets it (every
        // section that existed before this slice) renders identically
        // to before - only the Home sections that explicitly opt in
        // (see StremioVideoCenterView.xaml.cs) show a link at all.

        public string MoreLabel
        {
            get => (string)GetValue(MoreLabelProperty);
            set => SetValue(MoreLabelProperty, value);
        }

        public static readonly DependencyProperty MoreLabelProperty =
            DependencyProperty.Register(
                nameof(MoreLabel),
                typeof(string),
                typeof(MediaSection),
                new PropertyMetadata("More"));

        public Visibility MoreButtonVisibility
        {
            get => (Visibility)GetValue(MoreButtonVisibilityProperty);
            set => SetValue(MoreButtonVisibilityProperty, value);
        }

        public static readonly DependencyProperty MoreButtonVisibilityProperty =
            DependencyProperty.Register(
                nameof(MoreButtonVisibility),
                typeof(Visibility),
                typeof(MediaSection),
                new PropertyMetadata(Visibility.Collapsed));

        public event EventHandler MoreClicked;

        private void MoreButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            MoreClicked?.Invoke(this, EventArgs.Empty);
        }

        // =========================================================
        // FOCUS FIRST CARD
        // =========================================================

        public void FocusFirstCard()
        {
            FocusCard(0);
        }

        // =========================================================
        // FOCUS LAST CARD
        // =========================================================

        public void FocusLastCard()
        {
            int count =
                MediaItemsControl.Items.Count;

            if (count > 0)
            {
                FocusCard(count - 1);
            }
        }

        // =========================================================
        // FOCUS CARD AT (navigation stabilization pass)
        // =========================================================
        //
        // Used when vertical Up/Down navigation moves into this section
        // from a different section/row: the target column position is
        // preserved where practical (e.g. focus was on card 3 of the
        // previous row, land on card 3 here) instead of always resetting
        // to the first card, falling back safely to the closest valid
        // card when this section has fewer items than the requested
        // index.

        public void FocusCardAt(int index)
        {
            int count =
                MediaItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int clampedIndex =
                Math.Max(
                    0,
                    Math.Min(
                        index,
                        count - 1));

            FocusCard(clampedIndex);
        }

        // =========================================================
        // CURRENTLY FOCUSED CARD INDEX (public read)
        // =========================================================
        //
        // Exposes GetFocusedCardIndex() to callers outside this control
        // (StremioVideoCenterView's vertical navigation) that need to
        // know which column is focused before moving to another section,
        // without duplicating the focused-element resolution logic.

        public int GetFocusedIndex()
        {
            return GetFocusedCardIndex();
        }

        // =========================================================
        // HORIZONTAL NAVIGATION
        // =========================================================

        public void MoveHorizontal(int direction)
        {
            int count =
                MediaItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int currentIndex =
                GetFocusedCardIndex();

            if (currentIndex < 0)
            {
                currentIndex =
                    direction > 0
                        ? 0
                        : count - 1;
            }
            else
            {
                currentIndex += direction;
            }

            currentIndex =
                Math.Max(
                    0,
                    Math.Min(
                        currentIndex,
                        count - 1));

            FocusCard(currentIndex);
        }

        // =========================================================
        // FIND CURRENTLY FOCUSED CARD
        // =========================================================
        //
        // Navigation stabilization pass: this used to scan every item's
        // container index-by-index looking for one whose visual subtree
        // reported IsKeyboardFocusWithin. That re-derives "which index is
        // focused" indirectly (index -> container -> visual search ->
        // focus check) instead of asking WPF directly. It now goes the
        // other way - start from the actual focused element (the real
        // source of truth for "what has focus") and ask the ItemsControl
        // for that element's own container and index. This removes any
        // possibility of the scan matching the wrong index if a
        // container/index mapping is ever stale, and is also fewer
        // visual-tree walks than checking every item on every arrow key.

        private int GetFocusedCardIndex()
        {
            MediaCard focusedCard =
                Keyboard.FocusedElement as MediaCard;

            if (focusedCard == null)
            {
                return -1;
            }

            DependencyObject container =
                ItemsControl.ContainerFromElement(
                    MediaItemsControl,
                    focusedCard);

            if (container == null)
            {
                return -1;
            }

            int index =
                MediaItemsControl.ItemContainerGenerator
                    .IndexFromContainer(container);

            return index;
        }

        // =========================================================
        // FOCUS CARD
        // =========================================================

        // Lifecycle fix: bounded, self-terminating version of the previous
        // unconditional retry. WPF can legitimately need a handful of
        // dispatcher ticks to realize a container for a valid item right
        // after ItemsSource changes - that case is still retried here,
        // deferred at Loaded priority, same as before. What's no longer
        // possible is retrying forever: an index that is out of range for
        // the section's current item count (in particular index 0 against
        // an empty section, e.g. MyList/ContinueWatching before Slice
        // 16/17 ever populate them) is rejected immediately, since waiting
        // can never make a container exist for an item that isn't there -
        // this was previously an infinite Dispatcher.BeginInvoke loop
        // (ContainerFromIndex never stops returning null for an
        // impossible index). A section that has been unloaded (window/
        // view closed while a retry was pending) also stops rather than
        // rescheduling into a control that's gone. The retry count itself
        // is capped at MaxFocusCardRetries so even a genuinely stuck
        // container-generation case (not currently known to happen) gives
        // up instead of running forever.
        private const int MaxFocusCardRetries = 10;

        private void FocusCard(int index)
        {
            FocusCard(index, MaxFocusCardRetries);
        }

        private void FocusCard(int index, int attemptsRemaining)
        {
            if (!IsLoaded)
            {
                return;
            }

            int count =
                MediaItemsControl.Items.Count;

            if (index < 0 || index >= count)
            {
                // Impossible index - there is, and never will be (without
                // ItemsSource changing first, which would be a fresh call
                // from the caller, not a reason to keep retrying this
                // one), a container to focus here.
                return;
            }

            ContentPresenter presenter =
                MediaItemsControl.ItemContainerGenerator
                    .ContainerFromIndex(index)
                as ContentPresenter;

            if (presenter == null)
            {
                if (attemptsRemaining <= 0)
                {
                    return;
                }

                Dispatcher.BeginInvoke(
                    new Action(() =>
                        FocusCard(index, attemptsRemaining - 1)),
                    System.Windows.Threading.DispatcherPriority.Loaded);

                return;
            }

            MediaCard card =
                FindVisualChild<MediaCard>(
                    presenter);

            if (card == null)
            {
                return;
            }

            card.Focus();

            ScrollCardIntoView(card);
        }

        // =========================================================
        // CHECK FIRST CARD VISIBILITY
        // =========================================================

        public bool IsFirstCardVisible(
            ScrollViewer scrollViewer)
        {
            if (scrollViewer == null)
            {
                return false;
            }

            if (MediaItemsControl.Items.Count == 0)
            {
                return false;
            }

            ContentPresenter presenter =
                MediaItemsControl.ItemContainerGenerator
                    .ContainerFromIndex(0)
                as ContentPresenter;

            if (presenter == null)
            {
                return false;
            }

            MediaCard card =
                FindVisualChild<MediaCard>(
                    presenter);

            if (card == null)
            {
                return false;
            }

            try
            {
                GeneralTransform transform =
                    card.TransformToAncestor(
                        scrollViewer);

                Point point =
                    transform.Transform(
                        new Point(0, 0));

                double cardTop =
                    point.Y;

                double cardBottom =
                    cardTop +
                    card.ActualHeight;

                double viewportTop =
                    0;

                double viewportBottom =
                    scrollViewer.ViewportHeight;

                return cardBottom > viewportTop &&
                       cardTop < viewportBottom;
            }
            catch
            {
                return false;
            }
        }

        // =========================================================
        // HORIZONTAL SCROLL
        // =========================================================

        private void ScrollCardIntoView(
            MediaCard card)
        {
            try
            {
                GeneralTransform transform =
                    card.TransformToAncestor(
                        MediaScrollViewer);

                Point point =
                    transform.Transform(
                        new Point(0, 0));

                double cardLeft =
                    point.X;

                double cardRight =
                    cardLeft +
                    card.ActualWidth;

                double viewportWidth =
                    MediaScrollViewer.ViewportWidth;

                double currentOffset =
                    MediaScrollViewer.HorizontalOffset;

                if (cardLeft < 0)
                {
                    MediaScrollViewer.ScrollToHorizontalOffset(
                        Math.Max(
                            0,
                            currentOffset +
                            cardLeft));
                }
                else if (cardRight > viewportWidth)
                {
                    MediaScrollViewer.ScrollToHorizontalOffset(
                        Math.Min(
                            MediaScrollViewer.ExtentWidth,
                            currentOffset +
                            (cardRight -
                             viewportWidth)));
                }
            }
            catch
            {
                // Layout may still be updating.
            }
        }

        // =========================================================
        // VISUAL TREE SEARCH
        // =========================================================

        private static T FindVisualChild<T>(
            DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (
                int i = 0;
                i < VisualTreeHelper.GetChildrenCount(
                    parent);
                i++)
            {
                DependencyObject child =
                    VisualTreeHelper.GetChild(
                        parent,
                        i);

                T result =
                    child as T;

                if (result != null)
                {
                    return result;
                }

                result =
                    FindVisualChild<T>(
                        child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }

    // =============================================================
    // MEDIA ITEM SELECTED EVENT ARGS
    // =============================================================

    public class MediaItemSelectedEventArgs :
        EventArgs
    {
        public MediaItem Item { get; }

        public MediaItemSelectedEventArgs(
            MediaItem item)
        {
            Item = item;
        }
    }
}