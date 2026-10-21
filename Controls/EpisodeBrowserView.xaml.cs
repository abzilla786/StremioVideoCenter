using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    public sealed class EpisodeSelectedEventArgs : EventArgs
    {
        public MediaItem Episode { get; private set; }

        public EpisodeSelectedEventArgs(MediaItem episode)
        {
            Episode = episode;
        }
    }

    // Season -> episode browsing overlay. LoadAsync(show, cinemetaService,
    // kitsuService) fetches the show's seasons/episodes, lets the user
    // pick a season, then an episode, and raises EpisodeSelected with a
    // fully-built episode-shaped MediaItem. StremioId is set to the
    // SHOW's IMDb id, with Season/Episode set separately - never a
    // pre-combined "id:s:e" string, so ContentRequest.BuildStremioId()
    // remains the single, authoritative place that assembles the Stremio
    // episode id.
    //
    // This control is Cinemeta-based for tt-based (Movie/TV) shows.
    // LoadAsync looks up the show's IMDb id (MediaItem.StremioId) with
    // CinemetaService.GetMetaAsync("series", ...) - Cinemeta needs no
    // credential and returns every episode/season for a series in one
    // call (its "videos" array, grouped here by season). If Cinemeta has
    // no data for the show, the browser shows its empty state.
    //
    // A Kitsu-sourced show (StremioId starting "kitsu:") is routed to an
    // entirely separate Kitsu load path instead (LoadAsync's own routing,
    // TryLoadFromKitsuAsync/LoadEpisodesFromKitsu) - Cinemeta has no
    // knowledge of a Kitsu id. Anime therefore uses the same Show ->
    // Season -> Episode browsing UI as TV.
    //
    // Reuses the same SeasonSummary/EpisodeDetails display
    // DTOs for both providers (they are plain, provider-agnostic
    // display shapes already - see BuildSeasonButton/BuildEpisodeButton
    // below, which are completely unchanged by either slice) so the
    // UI-building code never needs to know which provider supplied the
    // data - only the LOAD/season-switch methods differ per provider.
    //
    // Slice 12 polish: seasons (left) and episodes (right) are now two
    // permanently visible, independently scrolling vertical panels rather
    // than two full-view "pages" toggled one at a time. Selecting a season
    // loads its episodes into the right panel without hiding the season
    // list. Escape/B still preserves the original two-level back
    // behaviour, adapted to focus location rather than page visibility -
    // see HandleBack for the reasoning.
    //
    // Same architecture as SourceSelectorView: Grid+ScrollViewer overlay
    // (not a Popup), hand-rolled Button lists (not ListBox), one shared
    // activation path for mouse/keyboard/controller in each panel.
    //
    // This control never touches StreamResolverService, RealDebridService,
    // PlaybackSourceAggregationService, PlaybackSourceNormalizationService,
    // or MPV - it only ever produces a MediaItem for the existing
    // PlaySelectedMediaAsync path to consume, exactly like a movie
    // selection already does.
    public partial class EpisodeBrowserView : UserControl
    {
        private readonly List<Button> _seasonButtons = new List<Button>();
        private readonly List<Border> _seasonActiveIndicators = new List<Border>();
        private readonly List<Button> _episodeButtons = new List<Button>();

        private CinemetaService _cinemetaService;
        private KitsuService _kitsuService;
        private MediaItem _show;
        private string _showImdbId;
        private List<EpisodeDetails> _currentSeasonEpisodes;

        // Non-null only when Cinemeta successfully supplied this show's
        // episodes - the full flat "videos" list for the series, every
        // season included. When set, season switches are a local filter
        // (see LoadEpisodesFromCinemeta) rather than a new network call.
        // Null when this show is Kitsu-sourced instead.
        private List<CinemetaVideo> _cinemetaEpisodes;

        // Slice 15 (Anime Architecture Audit + Kitsu Migration + Provider
        // Role Correction). Non-null only when this show is Kitsu-sourced
        // (MediaItem.StremioId starts with "kitsu:") and Kitsu actually
        // supplied episodes - the full flat episode list for the anime,
        // every season included (see BuildSeasonSummariesFromKitsu/
        // LoadEpisodesFromKitsu). Mutually exclusive with
        // _cinemetaEpisodes - a show is routed to exactly one provider
        // chain, never both (see LoadAsync's own routing).
        private List<KitsuEpisodeResource> _kitsuEpisodes;

        // True only while _kitsuEpisodes is the active source - tells
        // BuildEpisodeMediaItem which of _show.StremioId ("kitsu:<id>",
        // used verbatim) vs _showImdbId ("tt...", combined with Season/
        // Episode by ContentRequest.BuildStremioId()) to put on the
        // resulting episode MediaItem.
        private bool _isKitsuSourced;

        public event EventHandler<EpisodeSelectedEventArgs> EpisodeSelected;
        public event EventHandler BrowsingCancelled;

        // The browser never reads MyListService itself - it only reports
        // that its (series-level) My List button was pressed. The caller
        // owns the service/profile and reports the result back through
        // SetMyListState.
        public event EventHandler MyListToggleRequested;

        public EpisodeBrowserView()
        {
            InitializeComponent();

            // Mouse click-and-drag scrolling (Slice 20 Stage 3).
            DragScroll.Attach(SeasonListScrollViewer, DragScrollAxis.Vertical);
            DragScroll.Attach(EpisodeListScrollViewer, DragScrollAxis.Vertical);
        }

        // =========================================================
        // LOAD
        // =========================================================

        public async Task LoadAsync(
            MediaItem show,
            CinemetaService cinemetaService,
            KitsuService kitsuService)
        {
            if (show == null)
            {
                throw new ArgumentNullException(nameof(show));
            }

            if (cinemetaService == null)
            {
                throw new ArgumentNullException(nameof(cinemetaService));
            }

            if (kitsuService == null)
            {
                throw new ArgumentNullException(nameof(kitsuService));
            }

            _show = show;
            _cinemetaService = cinemetaService;
            _kitsuService = kitsuService;
            _cinemetaEpisodes = null;
            _kitsuEpisodes = null;
            _isKitsuSourced = false;
            _showImdbId = null;

            // Slice 15 (Anime Architecture Audit + Kitsu Migration +
            // Provider Role Correction): a Kitsu-sourced show (StremioId
            // = "kitsu:<id>", set by AnimeBrowseView/SearchView's own
            // Kitsu-backed mapping) is routed entirely separately from
            // the tt-based Cinemeta chain below - Cinemeta has no
            // knowledge of a Kitsu id at all.
            if (!string.IsNullOrWhiteSpace(show.StremioId) &&
                show.StremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase))
            {
                bool loadedFromKitsu = await TryLoadFromKitsuAsync(show);

                if (!loadedFromKitsu)
                {
                    ShowEmptyState("Season information isn't available for this title.");
                }

                return;
            }

            bool loadedFromCinemeta = await TryLoadFromCinemetaAsync(show);

            if (!loadedFromCinemeta)
            {
                ShowEmptyState("Season information isn't available for this title.");
            }
        }

        // =========================================================
        // LOAD: KITSU (Slice 15 Kitsu Migration - Kitsu-sourced anime
        // only, see LoadAsync's own routing above)
        // =========================================================
        //
        // Returns true only when Kitsu actually supplied usable episode
        // data and the browser has already been populated - false means
        // "Kitsu had nothing for this id" (unreachable, or a genuinely
        // empty/malformed result), which LoadAsync reports as an empty
        // state.
        private async Task<bool> TryLoadFromKitsuAsync(MediaItem show)
        {
            string kitsuId = show.StremioId.Substring("kitsu:".Length);

            if (string.IsNullOrWhiteSpace(kitsuId))
            {
                return false;
            }

            List<KitsuEpisodeResource> episodes;

            try
            {
                episodes = await _kitsuService.GetEpisodesAsync(kitsuId);
            }
            catch (Exception ex)
            {
                // KitsuService itself is fail-soft (never throws) - this
                // is a defensive backstop only, matching
                // TryLoadFromCinemetaAsync's identical remark.
                StremioVideoCenterLogger.Warn(
                    "Loading Kitsu episodes failed: " + UserFacingErrors.ForLog(ex));

                episodes = null;
            }

            List<KitsuEpisodeResource> numbered =
                (episodes ?? new List<KitsuEpisodeResource>())
                    .Where(e => e != null && e.Attributes != null && e.Attributes.Number.HasValue)
                    .OrderBy(e => e.Attributes.Number.Value)
                    .ToList();

            if (numbered.Count == 0)
            {
                return false;
            }

            List<SeasonSummary> seasons = BuildSeasonSummariesFromKitsu(numbered);

            if (seasons.Count == 0)
            {
                return false;
            }

            _isKitsuSourced = true;
            _kitsuEpisodes = numbered;

            UpdateShowInfoText();

            BrowserContentGrid.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            BuildSeasonButtons(seasons);

            if (_seasonButtons.Count > 0)
            {
                SetActiveSeasonIndex(0);

                SeasonSummary firstSeason = _seasonButtons[0].Tag as SeasonSummary;

                if (firstSeason != null)
                {
                    LoadEpisodesFromKitsu(firstSeason.SeasonNumber);
                }

                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_seasonButtons[0])),
                    DispatcherPriority.Input);
            }

            return true;
        }

        // =========================================================
        // LOAD: CINEMETA (primary)
        // =========================================================
        //
        // Returns true only when Cinemeta actually supplied usable
        // season/episode data and the browser has already been
        // populated - false means "Cinemeta had nothing for this show"
        // (LoadAsync then shows the empty state).
        private async Task<bool> TryLoadFromCinemetaAsync(MediaItem show)
        {
            if (string.IsNullOrWhiteSpace(show.StremioId) ||
                !show.StremioId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            CinemetaMeta meta;

            try
            {
                meta = await _cinemetaService.GetMetaAsync("series", show.StremioId);
            }
            catch
            {
                // CinemetaService itself is fail-soft (never throws) - this
                // is a defensive backstop only, not the expected path.
                meta = null;
            }

            if (meta == null || meta.Videos == null || meta.Videos.Count == 0)
            {
                return false;
            }

            _showImdbId = show.StremioId;
            _cinemetaEpisodes = meta.Videos;

            List<SeasonSummary> seasons = BuildSeasonSummariesFromCinemeta(_cinemetaEpisodes);

            if (seasons.Count == 0)
            {
                // Cinemeta returned a meta object but with no usable
                // season/episode grouping - treated as "no data".
                _cinemetaEpisodes = null;
                _showImdbId = null;
                return false;
            }

            UpdateShowInfoText();

            BrowserContentGrid.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            BuildSeasonButtons(seasons);

            if (_seasonButtons.Count > 0)
            {
                SetActiveSeasonIndex(0);

                SeasonSummary firstSeason = _seasonButtons[0].Tag as SeasonSummary;

                if (firstSeason != null)
                {
                    LoadEpisodesFromCinemeta(firstSeason.SeasonNumber);
                }

                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_seasonButtons[0])),
                    DispatcherPriority.Input);
            }

            return true;
        }

        private void UpdateShowInfoText()
        {
            if (!string.IsNullOrWhiteSpace(_show.Title))
            {
                ShowInfoText.Text = _show.Title;
                ShowInfoText.Visibility = Visibility.Visible;
            }
            else
            {
                ShowInfoText.Visibility = Visibility.Collapsed;
            }
        }

        // =========================================================
        // SEASON PANEL
        // =========================================================
        //
        // Displayed in exactly the order supplied - never reordered,
        // never filtered. Season 0 (Specials), when present, is shown like
        // any other entry.

        private void BuildSeasonButtons(List<SeasonSummary> seasons)
        {
            SeasonListPanel.Children.Clear();
            _seasonButtons.Clear();
            _seasonActiveIndicators.Clear();

            foreach (SeasonSummary season in seasons)
            {
                if (season == null)
                {
                    continue;
                }

                Button button = BuildSeasonButton(season);
                SeasonListPanel.Children.Add(button);
                _seasonButtons.Add(button);
            }
        }

        private Button BuildSeasonButton(SeasonSummary season)
        {
            StackPanel content = new StackPanel();

            string label = !string.IsNullOrWhiteSpace(season.Name)
                ? season.Name
                : (season.SeasonNumber == 0 ? "Specials" : "Season " + season.SeasonNumber);

            content.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });

            if (season.EpisodeCount > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = season.EpisodeCount + (season.EpisodeCount == 1 ? " episode" : " episodes"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            // Persistent "this season is active" indicator - separate from
            // the focus-triggered border highlight in SeasonRowButtonStyle,
            // so it stays visible even when keyboard focus moves over to
            // the episode panel. Toggled by SetActiveSeasonIndex, not by
            // any style trigger.
            Border activeIndicator = new Border
            {
                Height = 3,
                Background = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63)),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            content.Children.Add(activeIndicator);
            _seasonActiveIndicators.Add(activeIndicator);

            Button button = new Button
            {
                Style = (Style)Resources["SeasonRowButtonStyle"],
                Content = content,
                Tag = season
            };

            button.Click += SeasonButton_Click;

            return button;
        }

        // Marks exactly one season as "active" (the one whose episodes are
        // currently shown in the right panel) and clears the indicator on
        // every other season button.
        private void SetActiveSeasonIndex(int index)
        {
            for (int i = 0; i < _seasonActiveIndicators.Count; i++)
            {
                _seasonActiveIndicators[i].Visibility =
                    i == index ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SeasonButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            SeasonSummary season = button.Tag as SeasonSummary;
            if (season == null)
            {
                return;
            }

            SetActiveSeasonIndex(_seasonButtons.IndexOf(button));

            // Cinemeta and Kitsu both already delivered every season's
            // episodes in one call (LoadAsync) - switching seasons is a
            // local filter, not a new network request.
            if (_isKitsuSourced && _kitsuEpisodes != null)
            {
                LoadEpisodesFromKitsu(season.SeasonNumber);
            }
            else if (_cinemetaEpisodes != null)
            {
                LoadEpisodesFromCinemeta(season.SeasonNumber);
            }
        }

        // =========================================================
        // EPISODE PANEL - CINEMETA (local filter, no network call)
        // =========================================================

        private void LoadEpisodesFromCinemeta(int seasonNumber)
        {
            List<CinemetaVideo> episodesInSeason =
                _cinemetaEpisodes
                    .Where(video => video != null && video.Season == seasonNumber)
                    .OrderBy(video => video.Number)
                    .ToList();

            if (episodesInSeason.Count == 0)
            {
                ShowEpisodePanelMessage("No episodes found for this season.");
                return;
            }

            _currentSeasonEpisodes =
                episodesInSeason
                    .Select(BuildEpisodeDetailsFromCinemeta)
                    .ToList();

            BuildEpisodeButtons(_currentSeasonEpisodes);

            if (_episodeButtons.Count > 0)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_episodeButtons[0])),
                    DispatcherPriority.Input);
            }
        }

        // Groups Cinemeta's flat per-series "videos" list into one
        // SeasonSummary per distinct season number. Name is
        // deliberately left blank - BuildSeasonButton already falls back
        // to "Season N" / "Specials" (season 0) when Name is empty,
        // exactly the label Cinemeta has no equivalent field for, so no
        // duplicate labelling logic is needed here.
        private static List<SeasonSummary> BuildSeasonSummariesFromCinemeta(
            List<CinemetaVideo> videos)
        {
            return videos
                .Where(video => video != null)
                .GroupBy(video => video.Season)
                .OrderBy(group => group.Key)
                .Select(group => new SeasonSummary
                {
                    SeasonNumber = group.Key,
                    EpisodeCount = group.Count()
                })
                .ToList();
        }

        // Maps one Cinemeta episode into the same EpisodeDetails
        // shape BuildEpisodeButton/BuildEpisodeMediaItem already consume -
        // these are plain, provider-agnostic display DTOs (see class
        // remarks), so no change to the UI-building code below is needed.
        private static EpisodeDetails BuildEpisodeDetailsFromCinemeta(CinemetaVideo video)
        {
            double voteAverage;
            double.TryParse(
                video.Rating,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out voteAverage);

            string overview = !string.IsNullOrWhiteSpace(video.Overview)
                ? video.Overview
                : video.Description;

            string airDate = !string.IsNullOrWhiteSpace(video.FirstAired)
                ? video.FirstAired
                : video.Released;

            return new EpisodeDetails
            {
                EpisodeNumber = video.Number,
                SeasonNumber = video.Season,
                Name = video.Name,
                Overview = overview,
                // Cinemeta thumbnails are already full, absolute URLs
                // (episodes.metahub.space).
                StillPath = video.Thumbnail,
                AirDate = NormalizeCinemetaDate(airDate),
                // Runtime: Cinemeta doesn't expose a per-episode runtime -
                // left null.
                VoteAverage = voteAverage
            };
        }

        // Cinemeta's firstAired/released values are full ISO 8601
        // timestamps (e.g. "2011-04-18T00:00:00.000Z"). Truncated to a plain
        // "yyyy-MM-dd" so BuildEpisodeMediaItem's "AirDate.Substring(0, 4)"
        // year extraction works for either provider.
        private static string NormalizeCinemetaDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            int separatorIndex = value.IndexOf('T');

            return separatorIndex > 0
                ? value.Substring(0, separatorIndex)
                : value;
        }

        // =========================================================
        // EPISODE PANEL - KITSU (local filter, no network call)
        // =========================================================
        //
        // Same shape as LoadEpisodesFromCinemeta above - Kitsu already
        // delivered every episode in one call (TryLoadFromKitsuAsync),
        // so a season switch is a local filter, never a new request.

        private void LoadEpisodesFromKitsu(int seasonNumber)
        {
            List<KitsuEpisodeResource> episodesInSeason =
                _kitsuEpisodes
                    .Where(episode => (episode.Attributes.SeasonNumber ?? 1) == seasonNumber)
                    .OrderBy(episode => episode.Attributes.Number.Value)
                    .ToList();

            if (episodesInSeason.Count == 0)
            {
                ShowEpisodePanelMessage("No episodes found for this season.");
                return;
            }

            _currentSeasonEpisodes =
                episodesInSeason
                    .Select(BuildEpisodeDetailsFromKitsu)
                    .ToList();

            BuildEpisodeButtons(_currentSeasonEpisodes);

            if (_episodeButtons.Count > 0)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_episodeButtons[0])),
                    DispatcherPriority.Input);
            }
        }

        // Groups Kitsu's flat per-anime episode list into one
        // SeasonSummary per distinct season number - mirrors
        // BuildSeasonSummariesFromCinemeta exactly. Kitsu's own
        // "seasonNumber" attribute is absent on many anime (most anime
        // are not season-structured the way Western TV is) - defaulted
        // to 1 in that case, a display grouping convenience only, never
        // invented data and never sent as part of any id (the Kitsu/
        // Stremio episode id uses the absolute episode "number" alone -
        // see KitsuService's own remarks).
        private static List<SeasonSummary> BuildSeasonSummariesFromKitsu(
            List<KitsuEpisodeResource> episodes)
        {
            return episodes
                .Where(episode => episode != null && episode.Attributes != null)
                .GroupBy(episode => episode.Attributes.SeasonNumber ?? 1)
                .OrderBy(group => group.Key)
                .Select(group => new SeasonSummary
                {
                    SeasonNumber = group.Key,
                    EpisodeCount = group.Count()
                })
                .ToList();
        }

        // Maps one Kitsu episode into the same EpisodeDetails shape
        // BuildEpisodeButton/BuildEpisodeMediaItem already consume - see
        // BuildEpisodeDetailsFromCinemeta's identical remark on these
        // being plain, provider-agnostic display DTOs. Kitsu's episode
        // resource carries no per-episode rating (unlike Cinemeta) -
        // VoteAverage is left at 0, which BuildEpisodeButton already
        // treats as "don't show a rating" for exactly that reason.
        private static EpisodeDetails BuildEpisodeDetailsFromKitsu(KitsuEpisodeResource episode)
        {
            string thumbnail =
                episode.Attributes.Thumbnail?.Original ??
                episode.Attributes.Thumbnail?.Large ??
                episode.Attributes.Thumbnail?.Medium;

            return new EpisodeDetails
            {
                EpisodeNumber = episode.Attributes.Number.Value,
                SeasonNumber = episode.Attributes.SeasonNumber ?? 1,
                Name = episode.Attributes.CanonicalTitle,
                Overview = episode.Attributes.Synopsis,
                StillPath = thumbnail,
                AirDate = episode.Attributes.AirDate,
                Runtime = episode.Attributes.Length,
                VoteAverage = 0
            };
        }

        // Replaces the episode panel's content with a single centred
        // message - used for a failure/empty result scoped to one season,
        // as opposed to ShowEmptyState's full-browser takeover. The season
        // panel is completely unaffected, so the user can just pick a
        // different season.
        private void ShowEpisodePanelMessage(string message)
        {
            EpisodeListPanel.Children.Clear();
            _episodeButtons.Clear();

            EpisodeListPanel.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 20, 20, 20)
            });
        }

        private void BuildEpisodeButtons(List<EpisodeDetails> episodes)
        {
            EpisodeListPanel.Children.Clear();
            _episodeButtons.Clear();

            foreach (EpisodeDetails episode in episodes)
            {
                if (episode == null)
                {
                    continue;
                }

                Button button = BuildEpisodeButton(episode);
                EpisodeListPanel.Children.Add(button);
                _episodeButtons.Add(button);
            }
        }

        // Slice 14 (Complete TV Experience): episode cards are now a
        // landscape thumbnail + info layout instead of text-only, so the
        // button's Content is a two-column Grid (thumbnail | text stack)
        // rather than a single StackPanel. EpisodeCardButtonStyle itself
        // (focus/hover/accent-bar template) is unchanged, so keyboard/
        // controller focus and activation behaviour are unaffected -
        // only what's inside the button changed.
        private Button BuildEpisodeButton(EpisodeDetails episode)
        {
            Grid rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border thumbnail = BuildEpisodeThumbnail(episode);
            Grid.SetColumn(thumbnail, 0);
            rootGrid.Children.Add(thumbnail);

            StackPanel content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(content, 1);

            string episodeCode =
                "S" + episode.SeasonNumber.ToString().PadLeft(2, '0') +
                "E" + episode.EpisodeNumber.ToString().PadLeft(2, '0');

            string primaryLabel = episodeCode +
                (!string.IsNullOrWhiteSpace(episode.Name) ? " — " + episode.Name : string.Empty);

            content.Children.Add(new TextBlock
            {
                Text = primaryLabel,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });

            List<string> metaParts = new List<string>();

            if (!string.IsNullOrWhiteSpace(episode.AirDate))
            {
                metaParts.Add(episode.AirDate);
            }

            if (episode.Runtime.HasValue && episode.Runtime.Value > 0)
            {
                metaParts.Add(episode.Runtime.Value + " min");
            }

            if (episode.VoteAverage > 0)
            {
                metaParts.Add("\u2605 " + episode.VoteAverage.ToString("0.0"));
            }

            if (metaParts.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = string.Join("   \u00B7   ", metaParts),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(episode.Overview))
            {
                content.Children.Add(new TextBlock
                {
                    Text = episode.Overview,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }

            rootGrid.Children.Add(content);

            Button button = new Button
            {
                Style = (Style)Resources["EpisodeCardButtonStyle"],
                Content = rootGrid,
                Tag = episode
            };

            button.Click += EpisodeButton_Click;

            return button;
        }

        // Landscape (16:9) thumbnail with a graceful fallback glyph
        // underneath - the fallback is always added first (bottom layer),
        // so a missing StillPath, a malformed URL, or a failed download
        // all just leave the fallback visible rather than showing a
        // broken-image control. Both providers' thumbnails are absolute
        // URLs.
        private const string EpisodeThumbnailFallbackGlyph = "🎬";

        private static Border BuildEpisodeThumbnail(EpisodeDetails episode)
        {
            Border container = new Border
            {
                Width = 160,
                Height = 90,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x29)),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            Grid grid = new Grid();

            grid.Children.Add(new TextBlock
            {
                Text = EpisodeThumbnailFallbackGlyph,
                FontSize = 26,
                Opacity = 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            string thumbnailUrl =
                !string.IsNullOrWhiteSpace(episode.StillPath) &&
                episode.StillPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? episode.StillPath
                    : null;

            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                BitmapImage bitmap = TryCreateBitmapImage(thumbnailUrl);

                if (bitmap != null)
                {
                    grid.Children.Add(new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.UniformToFill
                    });
                }
            }

            container.Child = grid;

            return container;
        }

        // WPF downloads a BitmapImage with a remote UriSource
        // asynchronously and simply renders nothing if the download
        // fails - no "broken image" glyph is ever shown natively, so a
        // malformed URL (caught here) or a failed download (left to WPF)
        // both degrade to the fallback glyph behind it. Never throws.
        private static BitmapImage TryCreateBitmapImage(string url)
        {
            try
            {
                Uri uri;

                if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                {
                    return null;
                }

                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnDemand;
                bitmap.EndInit();

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void EpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            EpisodeDetails episode = button.Tag as EpisodeDetails;
            if (episode == null)
            {
                return;
            }

            MediaItem episodeItem = BuildEpisodeMediaItem(episode);

            EventHandler<EpisodeSelectedEventArgs> handler = EpisodeSelected;
            if (handler != null)
            {
                handler(this, new EpisodeSelectedEventArgs(episodeItem));
            }
        }

        // =========================================================
        // BUILD EPISODE MEDIAITEM
        // =========================================================
        //
        // StremioId is the SHOW's IMDb id - Season/Episode are set
        // separately and never combined into a single string here.
        // ContentRequest.BuildStremioId() remains the one place that
        // assembles "<imdbId>:<season>:<episode>". Unchanged by this
        // layout polish.

        private MediaItem BuildEpisodeMediaItem(EpisodeDetails episode)
        {
            // Slice 15 (Anime Architecture Audit + Kitsu Migration +
            // Provider Role Correction): a Kitsu-sourced episode carries
            // the show's own "kitsu:<id>" StremioId verbatim (Season is
            // still stored below for this browser's own display text,
            // but PlaySelectedMediaAsync's Kitsu-id branch does not use
            // it when building the actual Stremio id - see its own
            // remarks and KitsuService's id-convention notes). Every
            // other path is completely unchanged: _showImdbId ("tt...",
            // combined with Season/Episode by ContentRequest.
            // BuildStremioId()).
            MediaItem episodeItem = new MediaItem
            {
                StremioId = _isKitsuSourced ? _show.StremioId : _showImdbId,
                Season = episode.SeasonNumber,
                Episode = episode.EpisodeNumber,
                Type = _show.Type,
                // Slice 17C: series genres ride along so History can record
                // them (history is series-level; never episode genres).
                Genres = _show.Genres != null ? new List<string>(_show.Genres) : null,
                Title = _show.Title +
                    (!string.IsNullOrWhiteSpace(episode.Name)
                        ? " - " + episode.Name
                        : " - S" + episode.SeasonNumber + "E" + episode.EpisodeNumber),
                Subtitle = "Season " + episode.SeasonNumber + " \u00B7 Episode " + episode.EpisodeNumber,
                // Slice 14: carries the episode's overview/air-date through
                // the existing ContentRequest/MediaItem pipeline so the
                // Source Selector can show full episode context without a
                // second network request - see SourceSelectorView.Populate.
                Overview = episode.Overview,
                ReleaseDate = episode.AirDate
            };

            if (!string.IsNullOrWhiteSpace(episode.AirDate) && episode.AirDate.Length >= 4)
            {
                episodeItem.Year = episode.AirDate.Substring(0, 4);
            }

            if (episode.VoteAverage > 0)
            {
                episodeItem.Rating = episode.VoteAverage.ToString("0.0");
            }

            if (!string.IsNullOrWhiteSpace(episode.StillPath) &&
                episode.StillPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Cinemeta and Kitsu thumbnails are full, absolute URLs.
                episodeItem.PosterPath = episode.StillPath;
            }
            else if (!string.IsNullOrWhiteSpace(_show.PosterPath))
            {
                episodeItem.PosterPath = _show.PosterPath;
            }

            return episodeItem;
        }

        // =========================================================
        // EMPTY / ERROR STATE (whole-browser takeover)
        // =========================================================

        private void ShowEmptyState(string message)
        {
            BrowserContentGrid.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyStateText.Text = message;

            Dispatcher.BeginInvoke(
                new Action(() => FocusElement(EmptyStateBackButton)),
                DispatcherPriority.Input);
        }

        // =========================================================
        // ACTIVATION / CANCELLATION
        // =========================================================

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            HandleBack();
        }

        // Set by the host once an episode has been picked and its sources are
        // being loaded. The browser stays mounted (and owns input) for that
        // time, so Back must cancel the load rather than navigate within it.
        public bool IsLoadingSelection { get; set; }

        public void SetMyListState(bool isInMyList)
        {
            MyListButton.Content = isInMyList ? "\u2713 IN MY LIST" : "+ MY LIST";
            MyListButton.ToolTip = isInMyList ? "Remove from My List" : "Add to My List";
        }

        private void MyListButton_Click(object sender, RoutedEventArgs e)
        {
            EventHandler handler = MyListToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // Adapted from the old two-page back behaviour to the new
        // always-both-visible layout: backing out of the episode panel
        // returns focus to the season panel (landing on the currently
        // active season) rather than cancelling outright, matching what
        // "Back from the episode list returns to the season list" meant
        // before this polish removed the page toggle. Backing out of the
        // season panel still cancels the whole browser.
        private void HandleBack()
        {
            // While the host is loading the picked episode, Back backs out of
            // that load straight away (it raises BrowsingCancelled) instead of
            // first moving focus to the season list.
            if (!IsLoadingSelection && IsFocusInEpisodePanel() && _seasonButtons.Count > 0)
            {
                Button activeSeasonButton = FindActiveSeasonButton();
                FocusElement(activeSeasonButton ?? _seasonButtons[0]);
                return;
            }

            RaiseBrowsingCancelled();
        }

        private void RaiseBrowsingCancelled()
        {
            EventHandler handler = BrowsingCancelled;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void EpisodeBrowserView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                    MoveVertical(-1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    MoveVertical(1);
                    e.Handled = true;
                    break;

                case Key.Left:
                    MoveHorizontal(-1);
                    e.Handled = true;
                    break;

                case Key.Right:
                    MoveHorizontal(1);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    ActivateFocused();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    HandleBack();
                    e.Handled = true;
                    break;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            switch (button)
            {
                case ControllerInput.DPadUp:
                    MoveVertical(-1);
                    return true;

                case ControllerInput.DPadDown:
                    MoveVertical(1);
                    return true;

                case ControllerInput.DPadLeft:
                    MoveHorizontal(-1);
                    return true;

                case ControllerInput.DPadRight:
                    MoveHorizontal(1);
                    return true;

                case ControllerInput.A:
                    ActivateFocused();
                    return true;

                case ControllerInput.B:
                    HandleBack();
                    return true;
            }

            return false;
        }

        // =========================================================
        // FOCUS MOVEMENT
        // =========================================================
        //
        // Both panels are visible at once, so "which list does Up/Down
        // move through" is determined by where keyboard focus actually is
        // right now, not by a separately-tracked "current page" - a single
        // source of truth rather than a flag that could drift out of sync
        // with reality.

        private bool IsFocusInEpisodePanel()
        {
            return _episodeButtons.Contains(Keyboard.FocusedElement as Button);
        }

        private bool IsFocusInSeasonPanel()
        {
            return _seasonButtons.Contains(Keyboard.FocusedElement as Button);
        }

        private void MoveVertical(int direction)
        {
            // The Close control sits above both panels (see
            // EpisodeBrowserView.xaml) and is always focusable, so it is
            // treated as one more stop in the same vertical chain rather
            // than a separate control: Up from the topmost item of
            // either list reaches Close, and Down from Close reaches the
            // first available list. This replaces the previous cyclic
            // wrap-around, which let Up from the top skip straight to
            // the bottom of the list (and vice versa) instead of
            // reaching Close.
            if (CloseButton.IsKeyboardFocused || MyListButton.IsKeyboardFocused)
            {
                if (direction > 0)
                {
                    FocusFirstAvailableList();
                }

                // Up while already on Close is a no-op - there is
                // nothing above it to move to.
                return;
            }

            List<Button> buttons = IsFocusInEpisodePanel() ? _episodeButtons : _seasonButtons;

            MoveWithinList(buttons, direction);
        }

        // Down from Close lands on the first available list. Seasons
        // take priority when present, matching the panel layout (season
        // list is the left/primary column), falling back to episodes
        // when there are no seasons.
        private void FocusFirstAvailableList()
        {
            if (_seasonButtons.Count > 0)
            {
                FocusElement(_seasonButtons[0]);
            }
            else if (_episodeButtons.Count > 0)
            {
                FocusElement(_episodeButtons[0]);
            }
        }

        private void MoveWithinList(List<Button> buttons, int direction)
        {
            if (buttons.Count == 0)
            {
                return;
            }

            Button current = Keyboard.FocusedElement as Button;
            int index = buttons.IndexOf(current);

            if (index < 0)
            {
                index = 0;
            }
            else
            {
                index += direction;

                if (index < 0)
                {
                    // Up from the topmost item reaches the always-visible
                    // Close control instead of wrapping to the bottom of
                    // the list.
                    FocusElement(CloseButton);
                    return;
                }

                if (index >= buttons.Count)
                {
                    // Down from the bottom-most item stays put - there is
                    // no further target below the list, so no
                    // wrap-around happens.
                    return;
                }
            }

            FocusElement(buttons[index]);
        }

        // Left moves focus from the episode panel back to the season
        // panel (landing on the currently active season, so the visible
        // "this is the one you're browsing" indicator lines up with
        // focus). Right moves from the season panel into the episode
        // panel's first item, if it has any.
        private void MoveHorizontal(int direction)
        {
            // Header: Left/Right moves between My List and Close only.
            if (MyListButton.IsKeyboardFocused || CloseButton.IsKeyboardFocused)
            {
                if (MyListButton.IsKeyboardFocused && direction > 0)
                {
                    FocusElement(CloseButton);
                }
                else if (CloseButton.IsKeyboardFocused && direction < 0)
                {
                    FocusElement(MyListButton);
                }

                return;
            }

            if (direction < 0)
            {
                if (IsFocusInEpisodePanel() && _seasonButtons.Count > 0)
                {
                    Button activeSeasonButton = FindActiveSeasonButton();
                    FocusElement(activeSeasonButton ?? _seasonButtons[0]);
                }
            }
            else if (direction > 0)
            {
                if (IsFocusInSeasonPanel() && _episodeButtons.Count > 0)
                {
                    FocusElement(_episodeButtons[0]);
                }
            }
        }

        private Button FindActiveSeasonButton()
        {
            for (int i = 0; i < _seasonActiveIndicators.Count; i++)
            {
                if (_seasonActiveIndicators[i].Visibility == Visibility.Visible)
                {
                    return _seasonButtons[i];
                }
            }

            return null;
        }

        private void ActivateFocused()
        {
            Button focused = Keyboard.FocusedElement as Button;

            if (focused != null)
            {
                focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        // Focuses the element and explicitly scrolls it into view.
        // BringIntoView() scrolls within the nearest ancestor ScrollViewer
        // - since the season and episode panels now each have their own,
        // this correctly scrolls only the panel the focused item actually
        // belongs to, never the other one.
        private static void FocusElement(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            element.Focus();
            Keyboard.Focus(element);
            element.BringIntoView();
        }
    }
}
