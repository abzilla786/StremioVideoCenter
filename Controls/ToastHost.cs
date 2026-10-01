using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // TOAST HOST (Slice 17C - lightweight in-app feedback)
    // =============================================================
    //
    // A single-slot, transient message: a new Show() replaces whatever is
    // displayed and restarts the timer. It never takes keyboard/controller
    // focus and is invisible to the mouse (IsHitTestVisible = false), so it
    // cannot interfere with any existing navigation. Not persisted - there
    // is no history. Code-only (no XAML) to keep it self-contained.
    public sealed class ToastHost : Border
    {
        private const double DisplaySeconds = 2.5;

        private readonly TextBlock _text;
        private readonly DispatcherTimer _timer;

        public ToastHost()
        {
            Focusable = false;
            IsHitTestVisible = false;
            KeyboardNavigation.SetIsTabStop(this, false);

            Visibility = Visibility.Collapsed;
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x24));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(20, 10, 20, 10);

            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Focusable = false,
                TextWrapping = TextWrapping.Wrap
            };

            Child = _text;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(DisplaySeconds)
            };

            _timer.Tick += (sender, args) => Hide();

            Unloaded += (sender, args) => _timer.Stop();
        }

        public void Show(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _text.Text = message;
            Visibility = Visibility.Visible;

            _timer.Stop();
            _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            Visibility = Visibility.Collapsed;
        }
    }
}
