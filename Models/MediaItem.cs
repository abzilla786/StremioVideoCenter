using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace StremioVideoCenter.Models
{
    public class MediaItem : INotifyPropertyChanged
    {
        private BitmapImage _posterImage;
        private bool _posterLoading;

        // =========================================================
        // MEDIA IDENTITY
        // =========================================================

        // IMDb ID used by Stremio.
        // Example: tt1254207
        public string StremioId { get; set; }

        // TMDB numeric ID.
        // We will use this later when the TMDB integration becomes
        // the primary source of media metadata.
        public int? TmdbId { get; set; }

        // =========================================================
        // BASIC MEDIA INFORMATION
        // =========================================================

        public string Title { get; set; }

        public string Subtitle { get; set; }

        public string PosterPath { get; set; }

        public string Rating { get; set; }

        public string Year { get; set; }

        public string Type { get; set; }

        // =========================================================
        // EPISODE IDENTITY (Slice 11)
        // =========================================================
        //
        // Null for a movie or a show-level MediaItem - both remain
        // completely unchanged from before this slice. Populated only for
        // a MediaItem representing one specific TV/anime episode, which
        // nothing in the project constructs yet (episode browsing UI is a
        // separate, later slice - this is foundation only).
        //
        // Deliberately nullable int, matching ContentRequest.Season/
        // .Episode exactly - StremioId continues to mean "the show's IMDb
        // id" for an episode-shaped MediaItem (unchanged meaning), with
        // these two carrying the season/episode numbers separately so
        // PlaySelectedMediaAsync can combine them via
        // ContentRequest.BuildStremioId() the same way ForTvEpisode
        // already does, rather than requiring a pre-combined id string
        // here.
        public int? Season { get; set; }

        public int? Episode { get; set; }

        public double Progress { get; set; }

        // =========================================================
        // HERO MEDIA
        // =========================================================

        public string BackdropUrl { get; set; }

        public string TrailerUrl { get; set; }

        // =========================================================
        // POSTER IMAGE
        // =========================================================

        public BitmapImage PosterImage
        {
            get => _posterImage;

            private set
            {
                _posterImage = value;

                OnPropertyChanged(
                    nameof(PosterImage));
            }
        }

        // =========================================================
        // PROPERTY CHANGED
        // =========================================================

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(
            string propertyName)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(
                    propertyName));
        }

        // =========================================================
        // LOAD POSTER
        // =========================================================

        public async Task LoadPosterAsync()
        {
            if (_posterLoading)
            {
                return;
            }

            if (PosterImage != null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(PosterPath))
            {
                return;
            }

            _posterLoading = true;

            try
            {
                byte[] imageData =
                    await Task.Run(() =>
                    {
                        ServicePointManager.SecurityProtocol =
                            SecurityProtocolType.Tls12;

                        using (var client = new WebClient())
                        {
                            client.Headers[
                                HttpRequestHeader.UserAgent] =
                                "StremioVideoCenter/1.0";

                            return client.DownloadData(
                                PosterPath);
                        }
                    });

                using (var stream =
                    new MemoryStream(imageData))
                {
                    var image =
                        new BitmapImage();

                    image.BeginInit();

                    image.CacheOption =
                        BitmapCacheOption.OnLoad;

                    image.StreamSource =
                        stream;

                    image.EndInit();

                    image.Freeze();

                    PosterImage = image;
                }
            }
            catch
            {
                PosterImage = null;
            }
            finally
            {
                _posterLoading = false;
            }
        }
    }
}