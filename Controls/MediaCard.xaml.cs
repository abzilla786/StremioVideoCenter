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
        }

        private async void MediaCard_Loaded(
            object sender,
            RoutedEventArgs e)
        {
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