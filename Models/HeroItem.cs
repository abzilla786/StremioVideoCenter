using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace StremioVideoCenter.Models
{
    public class HeroItem : INotifyPropertyChanged
    {
        private BitmapImage _backdropImage;
        private bool _backdropLoading;

        // =========================================================
        // TMDB IDENTIFICATION
        // =========================================================

        public int TmdbId { get; set; }

        public string Title { get; set; }
        public string Type { get; set; }
        public string Year { get; set; }
        public string Rating { get; set; }

        public string Synopsis { get; set; }

        public string PosterUrl { get; set; }

        public string BackdropUrl { get; set; }

        public string TrailerUrl { get; set; }

        public BitmapImage BackdropImage
        {
            get => _backdropImage;

            private set
            {
                _backdropImage = value;
                OnPropertyChanged(nameof(BackdropImage));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(
            string propertyName)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadBackdropAsync()
        {
            System.Diagnostics.Debug.WriteLine(
                "HERO BACKDROP LOAD STARTED: " + Title);

            if (_backdropLoading)
            {
                return;
            }

            if (BackdropImage != null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(BackdropUrl))
            {
                return;
            }

            _backdropLoading = true;

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
                                BackdropUrl);
                        }
                    });

                using (var stream =
                    new MemoryStream(imageData))
                {
                    var image = new BitmapImage();

                    image.BeginInit();

                    image.CacheOption =
                        BitmapCacheOption.OnLoad;

                    image.StreamSource =
                        stream;

                    image.EndInit();

                    image.Freeze();

                    BackdropImage = image;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "HERO BACKDROP FAILED: " +
                    Title +
                    " | " +
                    ex.ToString());

                BackdropImage = null;
            }
            finally
            {
                _backdropLoading = false;
            }
        }
    }
}