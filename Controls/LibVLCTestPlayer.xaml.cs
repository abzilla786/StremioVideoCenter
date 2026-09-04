using System;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace StremioVideoCenter.Controls
{
    public partial class LibVlcTestPlayer : UserControl
    {
        // =========================================================
        // LIBVLC COMPONENTS
        // =========================================================

        private LibVLC _libVLC;

        private MediaPlayer _mediaPlayer;

        private VideoView _videoView;

        private Media _media;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public LibVlcTestPlayer()
        {
            InitializeComponent();

            // ---------------------------------------------------------
            // INITIALIZE LIBVLC
            // ---------------------------------------------------------

            Core.Initialize();

            // ---------------------------------------------------------
            // CREATE LIBVLC INSTANCE
            // ---------------------------------------------------------

            _libVLC =
                new LibVLC();

            // ---------------------------------------------------------
            // CREATE MEDIA PLAYER
            // ---------------------------------------------------------

            _mediaPlayer =
                new MediaPlayer(
                    _libVLC);

            // ---------------------------------------------------------
            // PLAYBACK ERROR HANDLER
            // ---------------------------------------------------------

            _mediaPlayer.EncounteredError +=
                MediaPlayer_EncounteredError;

            // ---------------------------------------------------------
            // CREATE VIDEO VIEW
            //
            // The VideoView is the native LibVLC video surface.
            // ---------------------------------------------------------

            _videoView =
                new VideoView
                {
                    HorizontalAlignment =
                        System.Windows.HorizontalAlignment.Stretch,

                    VerticalAlignment =
                        System.Windows.VerticalAlignment.Stretch
                };

            // ---------------------------------------------------------
            // CONNECT MEDIA PLAYER TO VIDEO VIEW
            // ---------------------------------------------------------

            _videoView.MediaPlayer =
                _mediaPlayer;

            // ---------------------------------------------------------
            // ADD VIDEO VIEW TO HOST
            // ---------------------------------------------------------

            VideoHost.Children.Add(
                _videoView);
        }


        // =========================================================
        // LIBVLC LOGGING
        // =========================================================

        private void LibVLC_Log(
            object sender,
            LogEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                "[LibVLC] " +
                e.Level +
                " - " +
                e.Message);
        }


        // =========================================================
        // PLAYBACK ERROR
        // =========================================================

        private void MediaPlayer_EncounteredError(
            object sender,
            EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Windows.MessageBox.Show(
                    "LibVLC playback failed.",
                    "LibVLC Test Error");
            });
        }


        // =========================================================
        // PLAY TEST VIDEO
        // =========================================================

        public void PlayTestVideo(
            string url)
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            // ---------------------------------------------------------
            // DISPOSE PREVIOUS MEDIA
            // ---------------------------------------------------------

            _media?.Dispose();

            _media = null;

            // ---------------------------------------------------------
            // CREATE NEW MEDIA
            // ---------------------------------------------------------

            _media =
                new Media(
                    _libVLC,
                    url,
                    FromType.FromLocation);

            // ---------------------------------------------------------
            // START PLAYBACK
            // ---------------------------------------------------------

            _mediaPlayer.Play(
                _media);
        }


        // =========================================================
        // STOP PLAYBACK
        // =========================================================

        public void Stop()
        {
            if (_mediaPlayer == null)
            {
                return;
            }

            try
            {
                _mediaPlayer.Stop();
            }
            catch
            {
                // Ignore shutdown errors.
            }
        }


        // =========================================================
        // CLEANUP
        // =========================================================

        public void DisposePlayer()
        {
            // ---------------------------------------------------------
            // STOP PLAYBACK
            // ---------------------------------------------------------

            try
            {
                _mediaPlayer?.Stop();
            }
            catch
            {
                // Ignore shutdown errors.
            }

            // ---------------------------------------------------------
            // DISPOSE MEDIA
            // ---------------------------------------------------------

            _media?.Dispose();
            _media = null;

            // ---------------------------------------------------------
            // DISPOSE MEDIA PLAYER
            // ---------------------------------------------------------

            _mediaPlayer?.Dispose();
            _mediaPlayer = null;

            // ---------------------------------------------------------
            // DISPOSE LIBVLC
            // ---------------------------------------------------------

            _libVLC?.Dispose();
            _libVLC = null;
        }
    }
}