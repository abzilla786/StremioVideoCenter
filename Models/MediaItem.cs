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

        public string Title { get; set; }

        public string Subtitle { get; set; }

        public string PosterPath { get; set; }

        public string Rating { get; set; }

        public string Year { get; set; }

        public string Type { get; set; }

        public double Progress { get; set; }

        public BitmapImage PosterImage
        {
            get => _posterImage;

            private set
            {
                _posterImage = value;
                OnPropertyChanged(nameof(PosterImage));
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
                            client.Headers[HttpRequestHeader.UserAgent] =
                                "StremioVideoCenter/1.0";

                            return client.DownloadData(
                                PosterPath);
                        }
                    });

                using (var stream =
                    new MemoryStream(imageData))
                {
                    var image = new BitmapImage();

                    image.BeginInit();

                    image.CacheOption =
                        BitmapCacheOption.OnLoad;

                    image.StreamSource = stream;

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