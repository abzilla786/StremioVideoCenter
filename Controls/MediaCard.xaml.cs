using StremioVideoCenter.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenterControls
{
    public partial class MediaCard : UserControl
    {
        public MediaCard()
        {
            InitializeComponent();

            Focusable = true;

            PreviewGotKeyboardFocus +=
                MediaCard_PreviewGotKeyboardFocus;

            PreviewLostKeyboardFocus +=
                MediaCard_PreviewLostKeyboardFocus;

            Loaded += MediaCard_Loaded;
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

        private void MediaCard_PreviewGotKeyboardFocus(
            object sender,
            KeyboardFocusChangedEventArgs e)
        {
            CardBorder.BorderBrush = Brushes.White;
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