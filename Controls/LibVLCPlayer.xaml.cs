using System;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace StremioVideoCenter.Controls
{
    public partial class LibVlcPlayer : UserControl
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

        public LibVlcPlayer()
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
            // CREATE VIDEO VIEW
            // ---------------------------------------------------------

            _videoView =
                new VideoView
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,

                    VerticalAlignment =
                        VerticalAlignment.Stretch
                };

            // ---------------------------------------------------------
            // CONNECT MEDIA PLAYER
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
        // PLAY MEDIA
        // =========================================================

        public void PlayVideo(
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
            // STOP PREVIOUS MEDIA
            // ---------------------------------------------------------

            try
            {
                _mediaPlayer.Stop();
            }
            catch
            {
                // Ignore stop errors while switching media.
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
