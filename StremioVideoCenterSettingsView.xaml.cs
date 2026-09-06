using StremioVideoCenter.Services;
using System.Windows;
using System.Windows.Controls;

namespace StremioVideoCenter
{
    public partial class StremioVideoCenterSettingsView : UserControl
    {
        private bool isLoadingValues = false;

        // =========================================================
        // CONSTRUCTOR
        // =========================================================

        public StremioVideoCenterSettingsView()
        {
            InitializeComponent();

            Loaded += SettingsView_Loaded;
        }

        // =========================================================
        // LOAD SAVED VALUES
        // =========================================================

        private void SettingsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            LoadSettingsIntoPasswordBoxes();
        }

        // =========================================================
        // LOAD SETTINGS INTO UI
        // =========================================================

        private void LoadSettingsIntoPasswordBoxes()
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel == null ||
                viewModel.Settings == null)
            {
                return;
            }

            isLoadingValues = true;

            TmdbApiKeyBox.Password =
                viewModel.Settings.TmdbApiKey ?? string.Empty;

            OmdbApiKeyBox.Password =
                viewModel.Settings.OmdbApiKey ?? string.Empty;

            isLoadingValues = false;
        }

        // =========================================================
        // TMDB PASSWORD CHANGED
        // =========================================================

        private void TmdbApiKeyBox_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (isLoadingValues)
            {
                return;
            }

            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            viewModel.Settings.TmdbApiKey =
                TmdbApiKeyBox.Password;

            TmdbConnectionStatus.Text =
                string.Empty;
        }

        // =========================================================
        // OMDB PASSWORD CHANGED
        // =========================================================

        private void OmdbApiKeyBox_PasswordChanged(
            object sender,
            RoutedEventArgs e)
        {
            if (isLoadingValues)
            {
                return;
            }

            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            viewModel.Settings.OmdbApiKey =
                OmdbApiKeyBox.Password;

            OmdbConnectionStatus.Text =
                string.Empty;
        }

        // =========================================================
        // TEST TMDB CONNECTION
        // =========================================================

        private async void TestTmdbConnection_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string accessToken =
                viewModel.Settings.TmdbApiKey;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                TmdbConnectionStatus.Text =
                    "Please enter your TMDB API Read Access Token first.";

                return;
            }

            TmdbConnectionStatus.Text =
                "Testing TMDB connection...";

            TestTmdbConnectionButton.IsEnabled = false;

            try
            {
                var tmdbService =
                    new TmdbService(accessToken);

                bool connected =
                    await tmdbService.TestConnectionAsync();

                if (connected)
                {
                    TmdbConnectionStatus.Text =
                        "TMDB connection successful.";
                }
                else
                {
                    TmdbConnectionStatus.Text =
                        "TMDB connection failed. Please check your credential and internet connection.";
                }
            }
            catch
            {
                TmdbConnectionStatus.Text =
                    "TMDB connection failed.";
            }
            finally
            {
                TestTmdbConnectionButton.IsEnabled = true;
            }
        }
        // =========================================================
        // TEST TMDB VIDEO DISCOVERY
        // =========================================================

        private async void TestTmdbDiscovery_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string accessToken =
                viewModel.Settings.TmdbApiKey;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                TmdbDiscoveryStatus.Text =
                    "Please enter your TMDB API Read Access Token first.";

                return;
            }

            TmdbDiscoveryStatus.Text =
                "Testing TMDB video discovery...";

            TestTmdbDiscoveryButton.IsEnabled = false;

            try
            {
                // =========================================================
                // CREATE TMDB SERVICES
                // =========================================================

                var tmdbService =
                    new TmdbService(accessToken);

                var recommendationService =
                    new HeroRecommendationService(
                        tmdbService);

                // =========================================================
                // GET FIVE HERO RECOMMENDATIONS
                // =========================================================

                var recommendations =
                    await recommendationService
                        .GetRecommendationsAsync();

                if (recommendations == null ||
                    recommendations.Count == 0)
                {
                    TmdbDiscoveryStatus.Text =
                        "No usable Hero recommendations were returned.";

                    return;
                }

                // =========================================================
                // CHECK VIDEO INFORMATION
                // =========================================================

                var results =
                    new System.Collections.Generic.List<string>();

                foreach (var hero in recommendations)
                {
                    bool isTv =
                        string.Equals(
                            hero.Type,
                            "TV",
                            System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            hero.Type,
                            "Anime",
                            System.StringComparison.OrdinalIgnoreCase);

                    var video =
                        await tmdbService
                            .GetBestTrailerOrTeaserAsync(
                                hero.TmdbId,
                                isTv);

                    if (video == null)
                    {
                        results.Add(
                            hero.Title +
                            " → No suitable trailer/teaser");
                    }
                    else
                    {
                        string officialText =
                            video.Official
                                ? "Official "
                                : string.Empty;

                        results.Add(
                            hero.Title +
                            " → " +
                            officialText +
                            video.Type +
                            " (" +
                            video.Site +
                            ")");
                    }
                }

                // =========================================================
                // DISPLAY RESULTS
                // =========================================================

                TmdbDiscoveryStatus.Text =
                    "TMDB video discovery successful:\n\n" +
                    string.Join(
                        "\n",
                        results);
            }
            catch
            {
                TmdbDiscoveryStatus.Text =
                    "TMDB video discovery failed. Please check your credential and internet connection.";
            }
            finally
            {
                TestTmdbDiscoveryButton.IsEnabled = true;
            }
        }

        // =========================================================
        // TEST OMDB CONNECTION
        // =========================================================

        private async void TestOmdbConnection_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string apiKey =
                viewModel.Settings.OmdbApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                OmdbConnectionStatus.Text =
                    "Please enter your OMDb API Key first.";

                return;
            }

            OmdbConnectionStatus.Text =
                "Testing OMDb connection...";

            TestOmdbConnectionButton.IsEnabled = false;

            try
            {
                var omdbService =
                    new OmdbService(apiKey);

                bool connected =
                    await omdbService.TestConnectionAsync();

                if (connected)
                {
                    OmdbConnectionStatus.Text =
                        "OMDb connection successful.";
                }
                else
                {
                    OmdbConnectionStatus.Text =
                        "OMDb connection failed. Please check your API key and internet connection.";
                }
            }
            catch
            {
                OmdbConnectionStatus.Text =
                    "OMDb connection failed.";
            }
            finally
            {
                TestOmdbConnectionButton.IsEnabled = true;
            }
        }
    }
}