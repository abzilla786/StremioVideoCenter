using StremioVideoCenter.Models;
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

            RealDebridApiKeyBox.Password =
                viewModel.Settings.RealDebridApiKey ?? string.Empty;

            StremioAddonUrlsBox.Text =
                viewModel.Settings.StremioAddonUrls ?? string.Empty;

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
        // REAL-DEBRID PASSWORD CHANGED
        // =========================================================

        private void RealDebridApiKeyBox_PasswordChanged(
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

            viewModel.Settings.RealDebridApiKey =
                RealDebridApiKeyBox.Password;

            RealDebridConnectionStatus.Text =
                string.Empty;
        }

        // =========================================================
        // STREMIO ADDON URLS CHANGED
        // =========================================================

        private void StremioAddonUrlsBox_TextChanged(
            object sender,
            TextChangedEventArgs e)
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

            viewModel.Settings.StremioAddonUrls =
                StremioAddonUrlsBox.Text;

            AddonTestStatus.Text =
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

        // =========================================================
        // TEST REAL-DEBRID CONNECTION
        // =========================================================

        private async void TestRealDebridConnection_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string apiToken =
                viewModel.Settings.RealDebridApiKey;

            if (string.IsNullOrWhiteSpace(apiToken))
            {
                RealDebridConnectionStatus.Text =
                    "Please enter your Real-Debrid API token first.";

                return;
            }

            RealDebridConnectionStatus.Text =
                "Testing Real-Debrid connection...";

            TestRealDebridConnectionButton.IsEnabled = false;

            try
            {
                var realDebridService =
                    new RealDebridService(apiToken);

                bool connected =
                    await realDebridService.TestConnectionAsync();

                if (connected)
                {
                    RealDebridConnectionStatus.Text =
                        "Real-Debrid connection successful.";
                }
                else
                {
                    RealDebridConnectionStatus.Text =
                        "Real-Debrid connection failed. Please check your API token and internet connection.";
                }
            }
            catch
            {
                RealDebridConnectionStatus.Text =
                    "Real-Debrid connection failed.";
            }
            finally
            {
                TestRealDebridConnectionButton.IsEnabled = true;
            }
        }

        // =========================================================
        // TEST CONTENT DISCOVERY (MOVIE / TV / ANIME)
        // =========================================================
        //
        // Proves the unified TMDB -> Stremio content-id pipeline for all
        // three content types against fixed, known-real TMDB ids:
        //   Movie: Fight Club            (TMDB 550  - same id TestConnectionAsync already uses)
        //   TV:    Breaking Bad S1E1     (TMDB 1396)
        //   Anime: Attack on Titan S1E1  (TMDB 1429 - Japan origin, Animation genre)
        //
        // Deliberately does NOT touch StreamResolverService or
        // RealDebridService, and never constructs a PlaybackSource or calls
        // the player - this proves discovery only, exactly as scoped.
        //
        // A stream count of 0 for the anime case is reported as a
        // limitation, not treated as a failure - the configured test addon
        // may simply not have anime content for an IMDb-based id, which is
        // an honest possible outcome, not a bug in this pipeline.

        private async void TestContentDiscovery_Click(
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
                ContentDiscoveryStatus.Text =
                    "Please enter your TMDB API Read Access Token first.";

                return;
            }

            ContentDiscoveryStatus.Text =
                "Testing content discovery (movie / TV / anime)...";

            TestContentDiscoveryButton.IsEnabled = false;

            try
            {
                var tmdbService =
                    new TmdbService(accessToken);

                var addonRegistry =
                    StremioAddonRegistry.FromConfiguredUrls(
                        viewModel.Settings.StremioAddonUrls);

                var discovery =
                    new StremioContentDiscoveryService(addonRegistry);

                var results =
                    new System.Collections.Generic.List<string>();

                // =========================================================
                // MOVIE: Fight Club (TMDB 550)
                // =========================================================

                try
                {
                    var movie =
                        await tmdbService.GetMovieDetailsAsync(550);

                    if (movie == null)
                    {
                        results.Add(
                            "Movie (Fight Club) → TMDB lookup failed.");
                    }
                    else if (string.IsNullOrWhiteSpace(movie.ImdbId))
                    {
                        results.Add(
                            "Movie (" +
                            movie.DisplayTitle +
                            ") → no IMDb id from TMDB.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForMovie(movie);

                        var streams =
                            await discovery.ResolveStreamsAsync(request);

                        results.Add(
                            "Movie (" +
                            movie.DisplayTitle +
                            ", " +
                            movie.ImdbId +
                            ") → " +
                            streams.Count +
                            " stream(s).");
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "Movie test failed: " +
                        ex.Message);
                }

                // =========================================================
                // TV: Breaking Bad S1E1 (TMDB 1396)
                // =========================================================

                try
                {
                    var show =
                        await tmdbService.GetTvDetailsAsync(1396);

                    var episode =
                        await tmdbService.GetTvEpisodeAsync(1396, 1, 1);

                    if (show == null || episode == null)
                    {
                        results.Add(
                            "TV (Breaking Bad) → TMDB lookup failed.");
                    }
                    else if (string.IsNullOrWhiteSpace(show.ImdbId))
                    {
                        results.Add(
                            "TV (" +
                            show.DisplayTitle +
                            ") → no IMDb id from TMDB.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForTvEpisode(
                                show,
                                episode,
                                isAnime: false);

                        var streams =
                            await discovery.ResolveStreamsAsync(request);

                        results.Add(
                            "TV (" +
                            show.DisplayTitle +
                            " S" +
                            episode.SeasonNumber +
                            "E" +
                            episode.EpisodeNumber +
                            ", " +
                            request.BuildStremioId() +
                            ") → " +
                            streams.Count +
                            " stream(s).");
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "TV test failed: " +
                        ex.Message);
                }

                // =========================================================
                // ANIME: Attack on Titan S1E1 (TMDB 1429)
                // =========================================================

                try
                {
                    var show =
                        await tmdbService.GetTvDetailsAsync(1429);

                    var episode =
                        await tmdbService.GetTvEpisodeAsync(1429, 1, 1);

                    if (show == null || episode == null)
                    {
                        results.Add(
                            "Anime (Attack on Titan) → TMDB lookup failed.");
                    }
                    else if (string.IsNullOrWhiteSpace(show.ImdbId))
                    {
                        results.Add(
                            "Anime (" +
                            show.DisplayTitle +
                            ") → no IMDb id from TMDB. This is a known " +
                            "limitation for some anime titles, not a bug.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForTvEpisode(
                                show,
                                episode,
                                isAnime: true);

                        var streams =
                            await discovery.ResolveStreamsAsync(request);

                        if (streams.Count == 0)
                        {
                            results.Add(
                                "Anime (" +
                                show.DisplayTitle +
                                " S" +
                                episode.SeasonNumber +
                                "E" +
                                episode.EpisodeNumber +
                                ") → 0 streams. The configured addon may " +
                                "not provide anime streams for an IMDb-" +
                                "based id - reported as a limitation, not " +
                                "a bug.");
                        }
                        else
                        {
                            results.Add(
                                "Anime (" +
                                show.DisplayTitle +
                                " S" +
                                episode.SeasonNumber +
                                "E" +
                                episode.EpisodeNumber +
                                ", " +
                                request.BuildStremioId() +
                                ") → " +
                                streams.Count +
                                " stream(s).");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "Anime test failed: " +
                        ex.Message);
                }

                // =========================================================
                // DISPLAY RESULTS
                // =========================================================

                ContentDiscoveryStatus.Text =
                    "Content discovery test complete:\n\n" +
                    string.Join(
                        "\n",
                        results);
            }
            catch (System.Exception ex)
            {
                ContentDiscoveryStatus.Text =
                    "Content discovery test failed: " +
                    ex.Message;
            }
            finally
            {
                TestContentDiscoveryButton.IsEnabled = true;
            }
        }

        // =========================================================
        // TEST CONFIGURED ADDONS
        // =========================================================
        //
        // Tests every configured addon's manifest independently - one
        // malformed or unreachable addon never prevents the others from
        // being tested or reported. Does not touch stream discovery or
        // playback; this is purely "can each configured addon be reached
        // and does it return a manifest."

        private async void TestConfiguredAddons_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            string urls =
                viewModel.Settings.StremioAddonUrls;

            if (string.IsNullOrWhiteSpace(urls))
            {
                AddonTestStatus.Text =
                    "Please enter at least one addon URL first.";

                return;
            }

            AddonTestStatus.Text =
                "Testing configured addons...";

            TestConfiguredAddonsButton.IsEnabled = false;

            try
            {
                var registry =
                    StremioAddonRegistry.FromConfiguredUrls(urls);

                if (registry.AddonUrls.Count == 0)
                {
                    AddonTestStatus.Text =
                        "No valid addon URLs were found.";

                    return;
                }

                var results =
                    await registry.TestAllAddonsAsync();

                var lines =
                    new System.Collections.Generic.List<string>();

                foreach (var result in results)
                {
                    lines.Add(
                        (result.Success ? "✓ " : "✗ ") +
                        result.AddonUrl +
                        " → " +
                        result.Message);
                }

                AddonTestStatus.Text =
                    "Addon test results:\n\n" +
                    string.Join(
                        "\n",
                        lines);
            }
            catch (System.Exception ex)
            {
                AddonTestStatus.Text =
                    "Addon test failed: " +
                    ex.Message;
            }
            finally
            {
                TestConfiguredAddonsButton.IsEnabled = true;
            }
        }

        // =========================================================
        // TEST SOURCE AGGREGATION (MOVIE / TV / ANIME)
        // =========================================================
        //
        // Proves the full Slice 4 backend end to end: configured addons ->
        // aggregated streams -> resolved candidate PlaybackSources (Direct
        // and Real-Debrid), deduplicated - using the same verified TMDB ids
        // as the Slice 2 content-discovery test:
        //   Movie: Fight Club            (TMDB 550)
        //   TV:    Breaking Bad S1E1     (TMDB 1396)
        //   Anime: Attack on Titan S1E1  (TMDB 1429)
        //
        // Does not send anything to MPV and does not touch any player file -
        // this proves the backend produces correct data, nothing more.

        private async void TestSourceAggregation_Click(
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
                SourceAggregationStatus.Text =
                    "Please enter your TMDB API Read Access Token first.";

                return;
            }

            SourceAggregationStatus.Text =
                "Testing source aggregation (movie / TV / anime)...";

            TestSourceAggregationButton.IsEnabled = false;

            try
            {
                var tmdbService =
                    new TmdbService(accessToken);

                var addonRegistry =
                    StremioAddonRegistry.FromConfiguredUrls(
                        viewModel.Settings.StremioAddonUrls);

                var discovery =
                    new StremioContentDiscoveryService(addonRegistry);

                RealDebridService realDebrid =
                    string.IsNullOrWhiteSpace(viewModel.Settings.RealDebridApiKey)
                        ? null
                        : new RealDebridService(viewModel.Settings.RealDebridApiKey);

                var resolver =
                    new StreamResolverService(realDebrid);

                var aggregator =
                    new PlaybackSourceAggregationService(
                        discovery,
                        resolver);

                var results =
                    new System.Collections.Generic.List<string>();

                // =========================================================
                // MOVIE: Fight Club (TMDB 550)
                // =========================================================

                try
                {
                    var movie =
                        await tmdbService.GetMovieDetailsAsync(550);

                    if (movie == null || string.IsNullOrWhiteSpace(movie.ImdbId))
                    {
                        results.Add(
                            "Movie (Fight Club) → no IMDb id or TMDB lookup " +
                            "failed, cannot test.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForMovie(movie);

                        var set =
                            await aggregator.AggregateAsync(request);

                        results.Add(
                            FormatAggregationResult(
                                "Movie (" + movie.DisplayTitle + ")",
                                set));
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "Movie test failed: " +
                        ex.Message);
                }

                // =========================================================
                // TV: Breaking Bad S1E1 (TMDB 1396)
                // =========================================================

                try
                {
                    var show =
                        await tmdbService.GetTvDetailsAsync(1396);

                    var episode =
                        await tmdbService.GetTvEpisodeAsync(1396, 1, 1);

                    if (show == null || episode == null ||
                        string.IsNullOrWhiteSpace(show.ImdbId))
                    {
                        results.Add(
                            "TV (Breaking Bad) → no IMDb id or TMDB lookup " +
                            "failed, cannot test.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForTvEpisode(
                                show,
                                episode,
                                isAnime: false);

                        var set =
                            await aggregator.AggregateAsync(request);

                        results.Add(
                            FormatAggregationResult(
                                "TV (" + show.DisplayTitle + " S1E1)",
                                set));
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "TV test failed: " +
                        ex.Message);
                }

                // =========================================================
                // ANIME: Attack on Titan S1E1 (TMDB 1429)
                // =========================================================

                try
                {
                    var show =
                        await tmdbService.GetTvDetailsAsync(1429);

                    var episode =
                        await tmdbService.GetTvEpisodeAsync(1429, 1, 1);

                    if (show == null || episode == null ||
                        string.IsNullOrWhiteSpace(show.ImdbId))
                    {
                        results.Add(
                            "Anime (Attack on Titan) → no IMDb id or TMDB " +
                            "lookup failed - a known limitation for some " +
                            "anime titles, not a bug.");
                    }
                    else
                    {
                        var request =
                            ContentRequest.ForTvEpisode(
                                show,
                                episode,
                                isAnime: true);

                        var set =
                            await aggregator.AggregateAsync(request);

                        results.Add(
                            FormatAggregationResult(
                                "Anime (" + show.DisplayTitle + " S1E1)",
                                set));
                    }
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "Anime test failed: " +
                        ex.Message);
                }

                // =========================================================
                // MOVIE: Big Buck Bunny (IMDb tt1254207)
                // =========================================================
                //
                // Exercises the Direct URL -> StreamResolverService ->
                // PlaybackSource path specifically against the known-working
                // local Hello World test addon. Uses the IMDb id directly
                // via a synthetic TmdbDetailsItem rather than a live TMDB
                // lookup - this case is about proving the addon/resolver
                // pipeline works end to end, not about TMDB's metadata
                // mapping for this specific title, so it deliberately does
                // not depend on TMDB having (or not having) a correct
                // external_ids entry for it.

                try
                {
                    var movie = new TmdbDetailsItem
                    {
                        Title = "Big Buck Bunny",
                        ExternalIds = new TmdbExternalIds
                        {
                            ImdbId = "tt1254207"
                        }
                    };

                    var request =
                        ContentRequest.ForMovie(movie);

                    var set =
                        await aggregator.AggregateAsync(request);

                    var addonUrlSet =
                        new System.Collections.Generic.HashSet<string>();

                    foreach (var source in set.Sources)
                    {
                        if (!string.IsNullOrWhiteSpace(source.SourceAddonUrl))
                        {
                            addonUrlSet.Add(source.SourceAddonUrl);
                        }
                    }

                    string addonUrlsText =
                        addonUrlSet.Count > 0
                            ? string.Join(", ", addonUrlSet)
                            : "(none)";

                    results.Add(
                        FormatAggregationResult(
                            "Movie (Big Buck Bunny, tt1254207)",
                            set) +
                        "\n   Addon(s): " +
                        addonUrlsText);
                }
                catch (System.Exception ex)
                {
                    results.Add(
                        "Big Buck Bunny test failed: " +
                        ex.Message);
                }

                // =========================================================
                // DISPLAY RESULTS
                // =========================================================

                SourceAggregationStatus.Text =
                    "Source aggregation test complete:\n\n" +
                    string.Join(
                        "\n\n",
                        results);
            }
            catch (System.Exception ex)
            {
                SourceAggregationStatus.Text =
                    "Source aggregation test failed: " +
                    ex.Message;
            }
            finally
            {
                TestSourceAggregationButton.IsEnabled = true;
            }
        }

        // =========================================================
        // FORMAT AGGREGATION RESULT
        // =========================================================

        private static string FormatAggregationResult(
            string label,
            PlaybackSourceSet set)
        {
            int direct = 0;
            int realDebrid = 0;

            foreach (var source in set.Sources)
            {
                if (source.Kind == PlaybackSourceKind.Direct)
                {
                    direct++;
                }
                else
                {
                    realDebrid++;
                }
            }

            string line =
                label +
                " → " +
                set.Sources.Count +
                " playable source(s) [" +
                direct +
                " direct, " +
                realDebrid +
                " Real-Debrid], " +
                set.Unresolved.Count +
                " unresolved.";

            if (set.Unresolved.Count > 0)
            {
                var reasons =
                    new System.Collections.Generic.List<string>();

                foreach (var unresolved in set.Unresolved)
                {
                    string name =
                        unresolved.Stream?.Name ??
                        unresolved.Stream?.Title ??
                        "(unnamed)";

                    reasons.Add(
                        "   - " +
                        name +
                        ": " +
                        unresolved.Reason);
                }

                line +=
                    "\n" +
                    string.Join(
                        "\n",
                        reasons);
            }

            return line;
        }

        // =========================================================
        // TEST SOURCE NORMALISATION
        // =========================================================
        //
        // Proves PlaybackSourceNormalizationService end to end against the
        // same known-working fixture as the Slice 4 Big Buck Bunny case:
        // a synthetic TmdbDetailsItem carrying tt1254207 directly (this
        // test is about the normalisation layer, not TMDB lookup fidelity),
        // resolved through the real aggregation pipeline, then normalised.
        //
        // Does not touch MPV, does not wire into live playback, and does
        // not rank/score sources - purely reports the normalised shape of
        // whatever the pipeline actually returns.

        private async void TestSourceNormalisation_Click(
            object sender,
            RoutedEventArgs e)
        {
            var viewModel =
                DataContext as StremioVideoCenterSettingsViewModel;

            if (viewModel?.Settings == null)
            {
                return;
            }

            SourceNormalisationStatus.Text =
                "Testing source normalisation (Big Buck Bunny)...";

            TestSourceNormalisationButton.IsEnabled = false;

            try
            {
                var addonRegistry =
                    StremioAddonRegistry.FromConfiguredUrls(
                        viewModel.Settings.StremioAddonUrls);

                var discovery =
                    new StremioContentDiscoveryService(addonRegistry);

                RealDebridService realDebrid =
                    string.IsNullOrWhiteSpace(viewModel.Settings.RealDebridApiKey)
                        ? null
                        : new RealDebridService(viewModel.Settings.RealDebridApiKey);

                var resolver =
                    new StreamResolverService(realDebrid);

                var aggregator =
                    new PlaybackSourceAggregationService(
                        discovery,
                        resolver);

                var normalizer =
                    new PlaybackSourceNormalizationService();

                var movie = new TmdbDetailsItem
                {
                    Title = "Big Buck Bunny",
                    ExternalIds = new TmdbExternalIds
                    {
                        ImdbId = "tt1254207"
                    }
                };

                var request =
                    ContentRequest.ForMovie(movie);

                var set =
                    await aggregator.AggregateAsync(request);

                var normalizedSources =
                    normalizer.NormalizeAll(set.Sources);

                var lines =
                    new System.Collections.Generic.List<string>();

                lines.Add("Big Buck Bunny");
                lines.Add(string.Empty);
                lines.Add("Playable Sources: " + normalizedSources.Count);

                int index = 1;

                foreach (var normalized in normalizedSources)
                {
                    lines.Add(string.Empty);
                    lines.Add("Source #" + index);
                    lines.Add("  Kind: " + normalized.Kind);
                    lines.Add("  Name: " + (normalized.Name ?? "Unknown"));
                    lines.Add("  Title: " + (normalized.Title ?? "Unknown"));
                    lines.Add("  Description: " + (normalized.Description ?? "Unknown"));
                    lines.Add("  Addon: " + (normalized.SourceAddonUrl ?? "Unknown"));
                    lines.Add("  URL: " + RedactUrlIfNeeded(normalized));

                    lines.Add(
                        "  Quality: " +
                        (normalized.ResolutionHeight.HasValue
                            ? normalized.ResolutionHeight.Value + "p"
                            : "Unknown"));

                    lines.Add(
                        "  File Size: " +
                        (normalized.FileSizeBytes.HasValue
                            ? FormatFileSize(normalized.FileSizeBytes.Value)
                            : "Unknown"));

                    lines.Add(
                        "  Release Filename: " +
                        (normalized.ReleaseFilename ?? "Unknown"));

                    lines.Add(
                        "  Release Name: " +
                        (normalized.ReleaseName ?? "Unknown"));

                    lines.Add(
                        "  Subtitle Languages: " +
                        (normalized.SubtitleLanguages != null &&
                         normalized.SubtitleLanguages.Count > 0
                            ? string.Join(", ", normalized.SubtitleLanguages)
                            : "Unknown"));

                    lines.Add("  HDR: " + (normalized.HdrInfo ?? "Unknown"));
                    lines.Add("  Video Codec: " + (normalized.CodecInfo ?? "Unknown"));
                    lines.Add("  Container: " + (normalized.ContainerInfo ?? "Unknown"));
                    lines.Add("  Audio Codec: " + (normalized.AudioInfo ?? "Unknown"));
                    lines.Add("  Atmos: " + (normalized.AtmosInfo ?? "Unknown"));
                    lines.Add("  Audio Channels: " + (normalized.AudioChannels ?? "Unknown"));

                    index++;
                }

                if (set.Unresolved.Count > 0)
                {
                    lines.Add(string.Empty);
                    lines.Add("Unresolved: " + set.Unresolved.Count);

                    foreach (var unresolved in set.Unresolved)
                    {
                        string name =
                            unresolved.Stream?.Name ??
                            unresolved.Stream?.Title ??
                            "(unnamed)";

                        lines.Add("  - " + name + ": " + unresolved.Reason);
                    }
                }

                SourceNormalisationStatus.Text =
                    string.Join("\n", lines);
            }
            catch (System.Exception ex)
            {
                SourceNormalisationStatus.Text =
                    "Source normalisation test failed: " +
                    ex.Message;
            }
            finally
            {
                TestSourceNormalisationButton.IsEnabled = true;
            }
        }

        // =========================================================
        // REDACT URL IF NEEDED
        // =========================================================
        //
        // Real-Debrid unrestricted links are effectively bearer-style
        // capability URLs - anyone with the link can stream it without
        // further authentication. Printing one in full in test output
        // would leak a usable download link, so only the host is shown.
        // Direct URLs (this addon's own plain HTTP links) carry no such
        // secret and are shown in full.

        private static string RedactUrlIfNeeded(NormalizedPlaybackSource normalized)
        {
            if (normalized.Kind != PlaybackSourceKind.RealDebrid)
            {
                return normalized.Url ?? "Unknown";
            }

            if (string.IsNullOrWhiteSpace(normalized.Url))
            {
                return "[redacted - Real-Debrid link]";
            }

            try
            {
                var uri = new System.Uri(normalized.Url);
                return uri.Scheme + "://" + uri.Host + "/... [redacted - Real-Debrid link]";
            }
            catch
            {
                return "[redacted - Real-Debrid link]";
            }
        }

        // =========================================================
        // FORMAT FILE SIZE
        // =========================================================

        private static string FormatFileSize(long bytes)
        {
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;

            if (gb >= 0.1)
            {
                return gb.ToString("0.0") + " GB";
            }

            double mb = bytes / 1024.0 / 1024.0;
            return mb.ToString("0") + " MB";
        }

        // =========================================================
        // RUN SELF-CHECKS
        // =========================================================
        //
        // Deterministic, dependency-free checks against the pure
        // normalization logic (PlaybackSourceNormalizationService.
        // RunSelfChecks) - no network call, no addon, no async needed.
        // Exists specifically to prove parsing edge cases like the DV/
        // DVDRip false-positive guard without depending on a real addon
        // happening to return a stream that exercises them.

        private void RunSelfChecks_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var results =
                    PlaybackSourceNormalizationService.RunSelfChecks();

                int passCount = 0;
                int failCount = 0;

                foreach (var result in results)
                {
                    if (result.StartsWith("PASS"))
                    {
                        passCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }

                var lines =
                    new System.Collections.Generic.List<string>();

                lines.Add(
                    passCount + " passed, " +
                    failCount + " failed/errored:");

                lines.Add(string.Empty);
                lines.AddRange(results);

                SelfCheckStatus.Text =
                    string.Join("\n", lines);
            }
            catch (System.Exception ex)
            {
                SelfCheckStatus.Text =
                    "Self-checks failed to run: " +
                    ex.Message;
            }
        }
    }
}