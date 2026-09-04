using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StremioVideoCenterControls
{
    public partial class MediaSection : UserControl
    {
        public MediaSection()
        {
            InitializeComponent();
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
            int count = MediaItemsControl.Items.Count;

            if (count > 0)
            {
                FocusCard(count - 1);
            }
        }

        // =========================================================
        // HORIZONTAL NAVIGATION
        // =========================================================

        public void MoveHorizontal(int direction)
        {
            int count = MediaItemsControl.Items.Count;

            if (count == 0)
            {
                return;
            }

            int currentIndex = GetFocusedCardIndex();

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

            currentIndex = Math.Max(
                0,
                Math.Min(
                    currentIndex,
                    count - 1));

            FocusCard(currentIndex);
        }

        // =========================================================
        // FIND CURRENTLY FOCUSED CARD
        // =========================================================

        private int GetFocusedCardIndex()
        {
            for (
                int i = 0;
                i < MediaItemsControl.Items.Count;
                i++)
            {
                ContentPresenter presenter =
                    MediaItemsControl.ItemContainerGenerator
                        .ContainerFromIndex(i)
                    as ContentPresenter;

                if (presenter == null)
                {
                    continue;
                }

                MediaCard card =
                    FindVisualChild<MediaCard>(presenter);

                if (card != null &&
                    card.IsKeyboardFocusWithin)
                {
                    return i;
                }
            }

            return -1;
        }

        // =========================================================
        // FOCUS CARD
        // =========================================================

        private void FocusCard(int index)
        {
            ContentPresenter presenter =
                MediaItemsControl.ItemContainerGenerator
                    .ContainerFromIndex(index)
                as ContentPresenter;

            if (presenter == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() =>
                        FocusCard(index)),
                    System.Windows.Threading.DispatcherPriority.Loaded);

                return;
            }

            MediaCard card =
                FindVisualChild<MediaCard>(presenter);

            if (card == null)
            {
                return;
            }

            card.Focus();

            ScrollCardIntoView(card);
        }

        // =========================================================
        // CHECK IF FIRST CARD IS VISIBLE
        // =========================================================

        public bool IsFirstCardVisible(ScrollViewer scrollViewer)
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
                FindVisualChild<MediaCard>(presenter);

            if (card == null)
            {
                return false;
            }

            try
            {
                GeneralTransform transform =
                    card.TransformToAncestor(scrollViewer);

                Point point =
                    transform.Transform(
                        new Point(0, 0));

                double cardTop = point.Y;

                double cardBottom =
                    cardTop + card.ActualHeight;

                double viewportTop = 0;

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

        private void ScrollCardIntoView(MediaCard card)
        {
            try
            {
                GeneralTransform transform =
                    card.TransformToAncestor(
                        MediaScrollViewer);

                Point point =
                    transform.Transform(
                        new Point(0, 0));

                double cardLeft = point.X;

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
                            currentOffset + cardLeft));
                }
                else if (cardRight > viewportWidth)
                {
                    MediaScrollViewer.ScrollToHorizontalOffset(
                        Math.Min(
                            MediaScrollViewer.ExtentWidth,
                            currentOffset +
                            (cardRight - viewportWidth)));
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
                i < VisualTreeHelper.GetChildrenCount(parent);
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
                    FindVisualChild<T>(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}