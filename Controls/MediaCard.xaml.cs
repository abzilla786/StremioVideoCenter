using StremioVideoCenter.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenterControls
{
    public partial class MediaCard : UserControl
    {
        public static readonly RoutedEvent SelectedEvent =
            EventManager.RegisterRoutedEvent(
                "Selected",
                RoutingStrategy.Bubble,
                typeof(RoutedEventHandler),
                typeof(MediaCard));

        public event RoutedEventHandler Selected
        {
            add
            {
                AddHandler(SelectedEvent, value);
            }
            remove
            {
                RemoveHandler(SelectedEvent, value);
            }
        }

        public MediaCard()
        {
            InitializeComponent();

            Focusable = true;

            PreviewGotKeyboardFocus +=
                MediaCard_PreviewGotKeyboardFocus;

            PreviewLostKeyboardFocus +=
                MediaCard_PreviewLostKeyboardFocus;

            PreviewMouseLeftButtonDown +=
                MediaCard_PreviewMouseLeftButtonDown;

            PreviewMouseLeftButtonUp +=
                MediaCard_PreviewMouseLeftButtonUp;

            MouseLeave +=
                MediaCard_MouseLeave;

            PreviewKeyDown +=
                MediaCard_PreviewKeyDown;

            Loaded +=
                MediaCard_Loaded;

            DataContextChanged +=
                MediaCard_DataContextChanged;
        }

        private void MediaCard_DataContextChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            UpdateProgressBar();
        }

        // Slice 18B: MediaItem.Progress is already the shared, clamped
        // PlaybackProgress value (0 = no bar), so this only renders it.
        private void UpdateProgressBar()
        {
            double progress = 0;

            if (DataContext is MediaItem mediaItem)
            {
                progress = mediaItem.Progress;
            }

            if (double.IsNaN(progress) || progress <= 0)
            {
                ProgressTrack.Visibility = Visibility.Collapsed;
                return;
            }

            if (progress > 1)
            {
                progress = 1;
            }

            ((ScaleTransform)ProgressFill.RenderTransform).ScaleX = progress;
            ProgressTrack.Visibility = Visibility.Visible;
        }

        private async void MediaCard_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            UpdateProgressBar();

            if (DataContext is MediaItem mediaItem)
            {
                await mediaItem.LoadPosterAsync();
            }
        }

        // Slice 21 (F5): a click selects only when the press began on this
        // card and the pointer has not travelled far since. Previously any
        // mouse-up over the card selected it, so releasing a short drag or
        // scroll gesture over a card (even one that began elsewhere)
        // activated that card unintentionally. DragScroll's own capture
        // already stops longer drags; this covers the rest.
        private const double ClickMoveTolerance = 8.0;

        private bool _pressedOnCard;
        private Point _pressPoint;

        private void MediaCard_PreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            _pressedOnCard = true;
            _pressPoint = e.GetPosition(this);
        }

        private void MediaCard_MouseLeave(
            object sender,
            MouseEventArgs e)
        {
            _pressedOnCard = false;
        }

        private void MediaCard_PreviewMouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            bool isClick = _pressedOnCard;

            if (isClick)
            {
                Point release = e.GetPosition(this);

                isClick =
                    Math.Abs(release.X - _pressPoint.X) <= ClickMoveTolerance &&
                    Math.Abs(release.Y - _pressPoint.Y) <= ClickMoveTolerance;
            }

            _pressedOnCard = false;

            if (isClick)
            {
                Focus();

                RaiseSelected();
            }

            e.Handled = true;
        }

        private void MediaCard_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            if (e.Key == Key.Enter ||
                e.Key == Key.Space)
            {
                RaiseSelected();

                e.Handled = true;
            }
        }

        private void RaiseSelected()
        {
            RaiseEvent(
                new RoutedEventArgs(
                    SelectedEvent));
        }

        private void MediaCard_PreviewGotKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            CardBorder.BorderBrush =
                Brushes.White;

            CardBorder.BorderThickness =
                new Thickness(3);
        }

        private void MediaCard_PreviewLostKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            CardBorder.BorderThickness =
                new Thickness(0);

            CardBorder.BorderBrush =
                Brushes.Transparent;
        }
    }
}