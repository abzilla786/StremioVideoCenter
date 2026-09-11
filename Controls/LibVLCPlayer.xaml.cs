using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StremioVideoCenter.Controls
{
    public partial class LibVlcPlayer : UserControl
    {
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private VideoView _videoView;
        private Media _media;

        private readonly DispatcherTimer _positionTimer;

        private bool _isSeeking;
        private bool _isMuted;
        private bool _closing;
        private bool _overlayAttached;

        private int _lastVolume = 100;

        public event EventHandler CloseRequested;

        public bool IsPlaying
        {
            get
            {
                return _mediaPlayer != null &&
                       _mediaPlayer.IsPlaying;
            }
        }


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public LibVlcPlayer()
        {
            InitializeComponent();

            Focusable = true;

            Core.Initialize();

            _libVLC =
                new LibVLC();

            _mediaPlayer =
                new MediaPlayer(
                    _libVLC);


            // =====================================================
            // CREATE VIDEO VIEW IN CODE
            // =====================================================

            _videoView =
                new VideoView
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,

                    VerticalAlignment =
                        VerticalAlignment.Stretch
                };

            _videoView.MediaPlayer =
                _mediaPlayer;


            // =====================================================
            // MOVE OVERLAY INTO VIDEOVIEW
            //
            // This is the important LibVLCSharp WPF airspace fix.
            // =====================================================

            PlayerRoot.Children.Remove(
                PlayerOverlay);

            _videoView.Content =
                PlayerOverlay;

            // LibVLCSharp renders VideoView content in its own overlay
            // window, so keyboard input must be handled by the overlay.
            PlayerOverlay.PreviewKeyDown +=
                PlayerOverlay_PreviewKeyDown;

            VideoHost.Children.Add(
                _videoView);

            _overlayAttached = true;


            // =====================================================
            // POSITION TIMER
            // =====================================================

            _positionTimer =
                new DispatcherTimer
                {
                    Interval =
                        TimeSpan.FromMilliseconds(250)
                };

            _positionTimer.Tick +=
                PositionTimer_Tick;

            _positionTimer.Start();


            Loaded +=
                LibVlcPlayer_Loaded;
        }


        // =========================================================
        // LOADED
        // =========================================================

        private void LibVlcPlayer_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            _closing = false;

            FocusPlayButton();

            UpdatePlaybackButtons();
        }


        // =========================================================
        // PLAY VIDEO
        // =========================================================

        public void PlayVideo(
            string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (_mediaPlayer == null ||
                _libVLC == null)
            {
                return;
            }

            _closing = false;

            try
            {
                _mediaPlayer.Stop();
            }
            catch
            {
                // Ignore stop errors.
            }


            if (_media != null)
            {
                try
                {
                    _media.Dispose();
                }
                catch
                {
                    // Ignore disposal errors.
                }

                _media = null;
            }


            _media =
                new Media(
                    _libVLC,
                    url,
                    FromType.FromLocation);


            _mediaPlayer.Play(
                _media);

            UpdatePlaybackButtons();
        }


        // =========================================================
        // PLAY / PAUSE
        // =========================================================

        private void PlayPauseButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            TogglePlayPause();

            e.Handled = true;
        }


        private void TogglePlayPause()
        {
            if (_mediaPlayer == null)
                return;

            try
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Pause();
                }
                else
                {
                    _mediaPlayer.Play();
                }
            }
            catch
            {
                // Ignore playback errors.
            }

            UpdatePlaybackButtons();
        }


        private void UpdatePlaybackButtons()
        {
            bool playing =
                _mediaPlayer != null &&
                _mediaPlayer.IsPlaying;

            string symbol =
                playing
                    ? "❚❚"
                    : "▶";


            if (PlayPauseButton != null)
            {
                PlayPauseButton.Content =
                    symbol;
            }


            if (CenterPlayButton != null)
            {
                CenterPlayButton.Content =
                    symbol;

                CenterPlayButton.Visibility =
                    playing
                        ? Visibility.Collapsed
                        : Visibility.Visible;
            }
        }


        // =========================================================
        // POSITION TIMER
        // =========================================================

        private void PositionTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (_closing)
                return;

            if (_mediaPlayer == null)
                return;

            long length =
                _mediaPlayer.Length;

            long time =
                _mediaPlayer.Time;

            if (length <= 0)
                return;


            if (!_isSeeking)
            {
                ProgressSlider.Maximum =
                    length;

                ProgressSlider.Value =
                    Math.Max(
                        0,
                        Math.Min(
                            time,
                            length));
            }


            TimeText.Text =
                FormatTime(time) +
                " / " +
                FormatTime(length);


            UpdatePlaybackButtons();
        }


        // =========================================================
        // TIMELINE
        // =========================================================

        private void ProgressSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSeeking)
                return;

            if (_mediaPlayer == null)
                return;

            if (ProgressSlider.Maximum <= 0)
                return;


            TimeText.Text =
                FormatTime(
                    (long)ProgressSlider.Value) +
                " / " +
                FormatTime(
                    (long)ProgressSlider.Maximum);
        }


        private void ProgressSlider_PreviewMouseDown(
            object sender,
            MouseButtonEventArgs e)
        {
            _isSeeking = true;
        }


        private void ProgressSlider_PreviewMouseUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (_mediaPlayer != null)
            {
                try
                {
                    _mediaPlayer.Time =
                        (long)ProgressSlider.Value;
                }
                catch
                {
                    // Ignore seek errors.
                }
            }

            _isSeeking = false;
        }


        // =========================================================
        // SEEK
        // =========================================================

        private void RewindButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            SeekRelative(-10000);

            e.Handled = true;
        }


        private void ForwardButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            SeekRelative(10000);

            e.Handled = true;
        }


        private void SeekRelative(
            long milliseconds)
        {
            if (_mediaPlayer == null)
                return;


            long current =
                Math.Max(
                    0,
                    _mediaPlayer.Time);

            long length =
                Math.Max(
                    0,
                    _mediaPlayer.Length);


            long target =
                current +
                milliseconds;


            target =
                Math.Max(
                    0,
                    Math.Min(
                        target,
                        length));


            try
            {
                _mediaPlayer.Time =
                    target;
            }
            catch
            {
                // Ignore seek errors.
            }
        }


        // =========================================================
        // VOLUME
        // =========================================================

        private void VolumeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null)
                return;


            int volume =
                (int)Math.Max(
                    0,
                    Math.Min(
                        100,
                        VolumeSlider.Value));


            try
            {
                _mediaPlayer.Volume =
                    volume;
            }
            catch
            {
                // Ignore volume errors.
            }


            if (volume > 0)
            {
                _lastVolume =
                    volume;


                if (_isMuted)
                {
                    _isMuted = false;

                    MuteButton.Content =
                        "🔊";
                }
            }
        }


        private void MuteButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;


            if (!_isMuted)
            {
                _lastVolume =
                    Math.Max(
                        1,
                        (int)VolumeSlider.Value);

                VolumeSlider.Value =
                    0;

                _isMuted = true;

                MuteButton.Content =
                    "🔇";
            }
            else
            {
                VolumeSlider.Value =
                    _lastVolume;

                _isMuted = false;

                MuteButton.Content =
                    "🔊";
            }

            e.Handled = true;
        }


        // =========================================================
        // CONTROL NAVIGATION
        // =========================================================

        private UIElement[] GetControls()
        {
            return new UIElement[]
            {
                PlayPauseButton,
                RewindButton,
                ForwardButton,
                MuteButton,
                VolumeSlider,
                FullscreenButton
            };
        }


        private int GetFocusedControlIndex()
        {
            UIElement[] controls =
                GetControls();


            for (int i = 0;
                 i < controls.Length;
                 i++)
            {
                if (controls[i] != null &&
                    controls[i].IsKeyboardFocusWithin)
                {
                    return i;
                }
            }


            return -1;
        }


        private void MoveControlFocus(
            int direction)
        {
            UIElement[] controls =
                GetControls();


            int current =
                GetFocusedControlIndex();


            if (current < 0)
            {
                FocusControl(
                    direction > 0
                        ? 0
                        : controls.Length - 1);

                return;
            }


            int next =
                current +
                direction;


            if (next < 0)
                next = controls.Length - 1;


            if (next >= controls.Length)
                next = 0;


            FocusControl(next);
        }


        private void FocusControl(
            int index)
        {
            UIElement[] controls =
                GetControls();


            if (index < 0 ||
                index >= controls.Length)
            {
                return;
            }


            controls[index]?.Focus();
        }


        private void FocusPlayButton()
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!_closing &&
                        PlayPauseButton != null)
                    {
                        PlayPauseButton.Focus();
                    }
                }),
                DispatcherPriority.Input);
        }


        // =========================================================
        // KEYBOARD / CONTROLLER
        // =========================================================

        private void PlayerOverlay_PreviewKeyDown(
            object sender,
            KeyEventArgs e)
        {
            if (_closing)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                RequestClose();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space ||
                e.Key == Key.Enter)
            {
                ActivateFocusedControl();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Left)
            {
                HandleLeftInput();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Right)
            {
                HandleRightInput();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                HandleUpInput();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                HandleDownInput();
                e.Handled = true;
                return;
            }
        }

        public bool HandleControllerInput(
            ControllerInput button)
        {
            if (_closing)
            {
                return true;
            }

            switch (button)
            {
                case ControllerInput.A:
                    ActivateFocusedControl();
                    return true;

                case ControllerInput.B:
                    RequestClose();
                    return true;

                case ControllerInput.DPadLeft:
                    HandleLeftInput();
                    return true;

                case ControllerInput.DPadRight:
                    HandleRightInput();
                    return true;

                case ControllerInput.DPadUp:
                    HandleUpInput();
                    return true;

                case ControllerInput.DPadDown:
                    HandleDownInput();
                    return true;

                default:
                    return false;
            }
        }

        private void ActivateFocusedControl()
        {
            if (ProgressSlider.IsKeyboardFocusWithin)
            {
                // A confirms the current timeline position.
                SeekToSliderPosition();
                return;
            }

            if (VolumeSlider.IsKeyboardFocusWithin)
            {
                ToggleMuteFromController();
                return;
            }

            int focusedIndex =
                GetFocusedControlIndex();

            if (focusedIndex < 0)
            {
                FocusPlayButton();
                return;
            }

            switch (focusedIndex)
            {
                case 0:
                    TogglePlayPause();
                    break;

                case 1:
                    SeekRelative(-10000);
                    break;

                case 2:
                    SeekRelative(10000);
                    break;

                case 3:
                    ToggleMuteFromController();
                    break;

                case 4:
                    VolumeSlider.Focus();
                    break;

                case 5:
                    FullscreenButton_Click(
                        FullscreenButton,
                        new RoutedEventArgs());
                    break;
            }
        }

        private void ToggleMuteFromController()
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            if (!_isMuted)
            {
                _lastVolume =
                    Math.Max(
                        1,
                        (int)VolumeSlider.Value);

                VolumeSlider.Value = 0;
                _isMuted = true;
                MuteButton.Content = "🔇";
            }
            else
            {
                VolumeSlider.Value =
                    Math.Max(
                        1,
                        Math.Min(
                            100,
                            _lastVolume));

                _isMuted = false;
                MuteButton.Content = "🔊";
            }
        }

        private void HandleLeftInput()
        {
            if (ProgressSlider.IsKeyboardFocusWithin)
            {
                SeekRelative(-10000);
                return;
            }

            if (VolumeSlider.IsKeyboardFocusWithin)
            {
                VolumeSlider.Value =
                    Math.Max(
                        0,
                        VolumeSlider.Value - 5);
                return;
            }

            MoveControlFocus(-1);
        }

        private void HandleRightInput()
        {
            if (ProgressSlider.IsKeyboardFocusWithin)
            {
                SeekRelative(10000);
                return;
            }

            if (VolumeSlider.IsKeyboardFocusWithin)
            {
                VolumeSlider.Value =
                    Math.Min(
                        100,
                        VolumeSlider.Value + 5);
                return;
            }

            MoveControlFocus(1);
        }

        private void HandleUpInput()
        {
            if (ProgressSlider.IsKeyboardFocusWithin)
            {
                FocusPlayButton();
                return;
            }

            if (GetFocusedControlIndex() >= 0)
            {
                ProgressSlider.Focus();
                return;
            }

            FocusPlayButton();
        }

        private void HandleDownInput()
        {
            if (ProgressSlider.IsKeyboardFocusWithin)
            {
                FocusPlayButton();
                return;
            }

            if (GetFocusedControlIndex() >= 0)
            {
                ProgressSlider.Focus();
                return;
            }

            FocusPlayButton();
        }

        private void SeekToSliderPosition()
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            try
            {
                _mediaPlayer.Time =
                    (long)ProgressSlider.Value;
            }
            catch
            {
                // Ignore seek errors.
            }

            _isSeeking = false;
        }

        // =========================================================
        // BACK
        // =========================================================

        private void BackButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            RequestClose();

            e.Handled = true;
        }


        private void RequestClose()
        {
            if (_closing)
                return;


            _closing = true;

            Stop();


            CloseRequested?.Invoke(
                this,
                EventArgs.Empty);
        }


        // =========================================================
        // FULLSCREEN
        // =========================================================

        private void FullscreenButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StatusText.Text =
                "Fullscreen will be enabled in the next player update.";

            e.Handled = true;
        }


        // =========================================================
        // TIME FORMAT
        // =========================================================

        private string FormatTime(
            long milliseconds)
        {
            if (milliseconds < 0)
                milliseconds = 0;


            TimeSpan time =
                TimeSpan.FromMilliseconds(
                    milliseconds);


            if (time.TotalHours >= 1)
            {
                return time.ToString(
                    @"hh\:mm\:ss");
            }


            return time.ToString(
                @"mm\:ss");
        }


        // =========================================================
        // STOP
        // =========================================================

        public void Stop()
        {
            if (_mediaPlayer == null)
                return;


            try
            {
                _mediaPlayer.Stop();
            }
            catch
            {
                // Ignore stop errors.
            }


            UpdatePlaybackButtons();
        }


        // =========================================================
        // DISPOSE
        // =========================================================

        public void DisposePlayer()
        {
            _closing = true;


            try
            {
                _positionTimer?.Stop();
            }
            catch
            {
                // Ignore timer errors.
            }


            // =====================================================
            // DETACH OVERLAY
            // =====================================================

            if (_videoView != null)
            {
                try
                {
                    if (PlayerOverlay != null)
                    {
                        PlayerOverlay.PreviewKeyDown -=
                            PlayerOverlay_PreviewKeyDown;
                    }

                    _videoView.Content =
                        null;
                }
                catch
                {
                    // Ignore overlay detach errors.
                }
            }


            _overlayAttached = false;


            // =====================================================
            // DETACH MEDIA PLAYER
            // =====================================================

            try
            {
                if (_videoView != null)
                {
                    _videoView.MediaPlayer =
                        null;
                }
            }
            catch
            {
                // Ignore detach errors.
            }


            // =====================================================
            // STOP VLC
            // =====================================================

            try
            {
                _mediaPlayer?.Stop();
            }
            catch
            {
                // Ignore stop errors.
            }


            // =====================================================
            // DISPOSE MEDIA
            // =====================================================

            try
            {
                _media?.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }


            _media = null;


            // =====================================================
            // DISPOSE MEDIA PLAYER
            // =====================================================

            try
            {
                _mediaPlayer?.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }


            _mediaPlayer = null;


            // =====================================================
            // REMOVE VIDEOVIEW
            // =====================================================

            try
            {
                if (_videoView != null &&
                    VideoHost != null)
                {
                    VideoHost.Children.Remove(
                        _videoView);
                }
            }
            catch
            {
                // Ignore removal errors.
            }


            _videoView = null;


            // =====================================================
            // DISPOSE LIBVLC
            // =====================================================

            try
            {
                _libVLC?.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }


            _libVLC = null;
        }
    }
}