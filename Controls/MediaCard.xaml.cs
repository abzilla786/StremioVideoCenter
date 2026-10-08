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

            PreviewMouseLeftButtonUp +=
                MediaCard_PreviewMouseLeftButtonUp;

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

        private void MediaCard_PreviewMouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            Focus();

            RaiseSelected();

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