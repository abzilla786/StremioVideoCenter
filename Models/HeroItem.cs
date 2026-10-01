using System;
using System.Collections.Generic;
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
        // CANONICAL IDENTITY (Slice 17 - TMDB removed from Hero)
        // =========================================================
        //
        // Replaces the old TmdbId/ImdbId pair. A Hero card is now
        // sourced from either Cinemeta (Movie/TV - id is the "tt..."
        // imdb id, same shape ContentRequest.BuildStremioId() itself
        // produces) or Kitsu (Anime - id is "kitsu:<id>", matching
        // every other Kitsu-sourced MediaItem.StremioId in this
        // project). Whichever it is, this is always the movie/show-
        // level id - no season/episode suffix, a Hero card is never a
        // specific episode.
        public string StremioId { get; set; }

        public string Title { get; set; }
        public string Type { get; set; }
        public string Year { get; set; }
        public string Rating { get; set; }

        public string Synopsis { get; set; }

        public string PosterUrl { get; set; }

        public string BackdropUrl { get; set; }

        public string TrailerUrl { get; set; }

        // =========================================================
        // SLICE 17B: IDENTITY/METADATA FOR WATCH NOW & MY LIST
        // =========================================================
        //
        // Added so a Hero card carries everything Watch Now (build a
        // ContentRequest/MediaItem and route through the existing
        // content-selection pathway) and My List (call MyListService with
        // the correct canonical identity) will need, without either of
        // those handlers making their own network call at click time.
        // StremioId above now carries this same role directly (Cinemeta
        // "tt..." or Kitsu "kitsu:<id>") - see HeroRecommendationService
        // for where it is set.

        // Genre names. For a Cinemeta-sourced (Movie/TV) card these are
        // Cinemeta's own real genre names (CinemetaCatalogItem.Genres),
        // copied straight through - no id-to-name mapping table needed,
        // unlike the old TMDB-based version. For a Kitsu-sourced (Anime)
        // card this is currently always empty - Kitsu's categories are a
        // relationship, not a plain attribute (see KitsuService's own
        // remarks), and resolving them for Hero specifically is out of
        // this slice's scope (Hero's own genre display is not part of the
        // Anime Browse filter work this slice covers).
        public List<string> Genres { get; set; }

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