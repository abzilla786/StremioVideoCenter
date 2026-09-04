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
        // PLAYER STATE
        // =========================================================

        // True when LibVLC has successfully started playback.
        private bool _isPlaying;

        // True when the player has been disposed.
        private bool _isDisposed;


        // =========================================================
        // PLAYBACK EVENTS
        // =========================================================

        // Fired when LibVLC begins playing the current media.
        public event EventHandler PlaybackStarted;

        // Fired when the current media reaches its natural end.
        public event EventHandler PlaybackEnded;

        // Fired when LibVLC encounters a playback error.
        public event EventHandler PlaybackError;


        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public LibVlcTestPlayer()
        {
            InitializeComponent();


            // =========================================================
            // INITIALIZE LIBVLC
            // =========================================================

            Core.Initialize();


            // =========================================================
            // CREATE LIBVLC INSTANCE
            // =========================================================

            _libVLC =
                new LibVLC();


            // =========================================================
            // CREATE MEDIA PLAYER
            // =========================================================

            _mediaPlayer =
                new MediaPlayer(
                    _libVLC);


            // =========================================================
            // PLAYBACK EVENTS
            // =========================================================

            _mediaPlayer.Playing +=
                MediaPlayer_Playing;

            _mediaPlayer.EndReached +=
                MediaPlayer_EndReached;

            _mediaPlayer.EncounteredError +=
                MediaPlayer_EncounteredError;


            // =========================================================
            // CREATE VIDEO VIEW
            //
            // The VideoView is the native LibVLC video surface.
            // =========================================================

            _videoView =
                new VideoView
                {
                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,

                    VerticalAlignment =
                        VerticalAlignment.Stretch
                };


            // =========================================================
            // CONNECT MEDIA PLAYER TO VIDEO VIEW
            // =========================================================

            _videoView.MediaPlayer =
                _mediaPlayer;


            // =========================================================
            // ADD VIDEO VIEW TO HOST
            // =========================================================

            VideoHost.Children.Add(
                _videoView);
        }


        // =========================================================
        // PLAYBACK STARTED
        // =========================================================

        private void MediaPlayer_Playing(
            object sender,
            EventArgs e)
        {
            _isPlaying = true;


            // ---------------------------------------------------------
            // NOTIFY THE HERO VIEW
            // ---------------------------------------------------------

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    PlaybackStarted?.Invoke(
                        this,
                        EventArgs.Empty);
                }));
        }


        // =========================================================
        // PLAYBACK ENDED
        // =========================================================

        private void MediaPlayer_EndReached(
            object sender,
            EventArgs e)
        {
            _isPlaying = false;


            // ---------------------------------------------------------
            // NOTIFY THE HERO VIEW
            // ---------------------------------------------------------

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    PlaybackEnded?.Invoke(
                        this,
                        EventArgs.Empty);
                }));
        }


        // =========================================================
        // PLAYBACK ERROR
        // =========================================================

        private void MediaPlayer_EncounteredError(
            object sender,
            EventArgs e)
        {
            _isPlaying = false;


            // ---------------------------------------------------------
            // NOTIFY THE HERO VIEW
            // ---------------------------------------------------------

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    PlaybackError?.Invoke(
                        this,
                        EventArgs.Empty);
                }));
        }


        // =========================================================
        // PLAY TEST VIDEO
        //
        // This method remains available so that our current
        // Big Buck Bunny test can continue to work.
        // =========================================================

        public void PlayTestVideo(
            string url)
        {
            PlayVideo(url);
        }


        // =========================================================
        // PLAY VIDEO
        // =========================================================

        public void PlayVideo(
            string url)
        {
            if (_mediaPlayer == null ||
                _isDisposed)
            {
                return;
            }


            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }


            // =========================================================
            // RESET PLAYBACK STATE
            // =========================================================

            _isPlaying = false;


            // =========================================================
            // STOP CURRENT MEDIA
            // =========================================================

            try
            {
                _mediaPlayer.Stop();
            }
            catch
            {
                // Ignore stop errors when replacing media.
            }


            // =========================================================
            // DISPOSE PREVIOUS MEDIA
            // =========================================================

            _media?.Dispose();

            _media = null;


            // =========================================================
            // CREATE NEW MEDIA
            // =========================================================

            _media =
                new Media(
                    _libVLC,
                    url,
                    FromType.FromLocation);


            // =========================================================
            // START PLAYBACK
            // =========================================================

            _mediaPlayer.Play(
                _media);
        }


        // =========================================================
        // IS PLAYING
        // =========================================================

        public bool IsPlaying
        {
            get
            {
                return _isPlaying;
            }
        }


        // =========================================================
        // STOP PLAYBACK
        // =========================================================

        public void Stop()
        {
            if (_mediaPlayer == null ||
                _isDisposed)
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


            _isPlaying = false;
        }


        // =========================================================
        // CLEANUP
        // =========================================================

        public void DisposePlayer()
        {
            if (_isDisposed)
            {
                return;
            }


            _isDisposed = true;


            // =========================================================
            // UNSUBSCRIBE EVENTS
            // =========================================================

            if (_mediaPlayer != null)
            {
                _mediaPlayer.Playing -=
                    MediaPlayer_Playing;

                _mediaPlayer.EndReached -=
                    MediaPlayer_EndReached;

                _mediaPlayer.EncounteredError -=
                    MediaPlayer_EncounteredError;
            }


            // =========================================================
            // STOP PLAYBACK
            // =========================================================

            try
            {
                _mediaPlayer?.Stop();
            }
            catch
            {
                // Ignore shutdown errors.
            }


            // =========================================================
            // DISPOSE MEDIA
            // =========================================================

            _media?.Dispose();

            _media = null;


            // =========================================================
            // DISPOSE MEDIA PLAYER
            // =========================================================

            _mediaPlayer?.Dispose();

            _mediaPlayer = null;


            // =========================================================
            // DISPOSE LIBVLC
            // =========================================================

            _libVLC?.Dispose();

            _libVLC = null;
        }
    }
}