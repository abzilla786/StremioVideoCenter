using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    // Season -> episode browsing overlay. LoadAsync(show, tmdbService)
    // fetches the show's seasons by reusing the existing TmdbService (no
    // new HTTP client, no new TMDB service, no duplicate Stremio request
    // logic anywhere in this class), lets the user pick a season, then an
    // episode, and raises EpisodeSelected with a fully-built episode-shaped
    // MediaItem. StremioId is set to the SHOW's IMDb id, with Season/
    // Episode set separately - never a pre-combined "id:s:e" string, so
    // ContentRequest.BuildStremioId() remains the single, authoritative
    // place that assembles the Stremio episode id.
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

        private TmdbService _tmdbService;
        private MediaItem _show;
        private string _showImdbId;
        private int _showTmdbId;
        private List<TmdbEpisodeDetails> _currentSeasonEpisodes;

        public event EventHandler<EpisodeSelectedEventArgs> EpisodeSelected;
        public event EventHandler BrowsingCancelled;

        public EpisodeBrowserView()
        {
            InitializeComponent();
        }

        // =========================================================
        // LOAD
        // =========================================================

        public async Task LoadAsync(MediaItem show, TmdbService tmdbService)
        {
            if (show == null)
            {
                throw new ArgumentNullException(nameof(show));
            }

            if (tmdbService == null)
            {
                throw new ArgumentNullException(nameof(tmdbService));
            }

            _show = show;
            _tmdbService = tmdbService;

            if (!show.TmdbId.HasValue)
            {
                ShowEmptyState("Season information isn't available for this title.");
                return;
            }

            _showTmdbId = show.TmdbId.Value;

            TmdbDetailsItem details;

            try
            {
                details = await _tmdbService.GetTvDetailsAsync(_showTmdbId);
            }
            catch (Exception ex)
            {
                ShowEmptyState("Couldn't load season information.\n\n" + ex.Message);
                return;
            }

            if (details == null || string.IsNullOrWhiteSpace(details.ImdbId))
            {
                ShowEmptyState("Season information isn't available for this title.");
                return;
            }

            _showImdbId = details.ImdbId;

            if (details.Seasons == null || details.Seasons.Count == 0)
            {
                ShowEmptyState("No seasons found.");
                return;
            }

            UpdateShowInfoText();

            BrowserContentGrid.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            BuildSeasonButtons(details.Seasons);

            // Auto-load the first season's episodes so the episode panel
            // isn't empty the moment the browser opens - both panels are
            // visible at once now, so there's no reason to make the user
            // take an extra action just to see something on the right.
            if (_seasonButtons.Count > 0)
            {
                SetActiveSeasonIndex(0);

                TmdbSeasonSummary firstSeason = _seasonButtons[0].Tag as TmdbSeasonSummary;

                if (firstSeason != null)
                {
                    await LoadEpisodesAsync(firstSeason);
                }

                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_seasonButtons[0])),
                    DispatcherPriority.Input);
            }
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
        // Displayed in exactly the order TMDB returns them - never
        // reordered, never filtered. Season 0 (Specials), when TMDB
        // provides it, is shown like any other entry.

        private void BuildSeasonButtons(List<TmdbSeasonSummary> seasons)
        {
            SeasonListPanel.Children.Clear();
            _seasonButtons.Clear();
            _seasonActiveIndicators.Clear();

            foreach (TmdbSeasonSummary season in seasons)
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

        private Button BuildSeasonButton(TmdbSeasonSummary season)
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

        private async void SeasonButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            TmdbSeasonSummary season = button.Tag as TmdbSeasonSummary;
            if (season == null)
            {
                return;
            }

            SetActiveSeasonIndex(_seasonButtons.IndexOf(button));

            await LoadEpisodesAsync(season);
        }

        // =========================================================
        // EPISODE PANEL
        // =========================================================

        private async Task LoadEpisodesAsync(TmdbSeasonSummary season)
        {
            TmdbSeasonDetails seasonDetails;

            try
            {
                seasonDetails = await _tmdbService.GetTvSeasonAsync(_showTmdbId, season.SeasonNumber);
            }
            catch (Exception ex)
            {
                // Per-season failure - the season list stays fully visible
                // and usable, only the episode panel shows the problem.
                ShowEpisodePanelMessage("Couldn't load episodes for this season.\n\n" + ex.Message);
                return;
            }

            if (seasonDetails == null || seasonDetails.Episodes == null || seasonDetails.Episodes.Count == 0)
            {
                ShowEpisodePanelMessage("No episodes found for this season.");
                return;
            }

            _currentSeasonEpisodes = seasonDetails.Episodes;

            BuildEpisodeButtons(_currentSeasonEpisodes);

            if (_episodeButtons.Count > 0)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusElement(_episodeButtons[0])),
                    DispatcherPriority.Input);
            }
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

        private void BuildEpisodeButtons(List<TmdbEpisodeDetails> episodes)
        {
            EpisodeListPanel.Children.Clear();
            _episodeButtons.Clear();

            foreach (TmdbEpisodeDetails episode in episodes)
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

        private Button BuildEpisodeButton(TmdbEpisodeDetails episode)
        {
            StackPanel content = new StackPanel();

            string primaryLabel = "Episode " + episode.EpisodeNumber +
                (!string.IsNullOrWhiteSpace(episode.Name) ? ": " + episode.Name : string.Empty);

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

            Button button = new Button
            {
                Style = (Style)Resources["EpisodeCardButtonStyle"],
                Content = content,
                Tag = episode
            };

            button.Click += EpisodeButton_Click;

            return button;
        }

        private void EpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            TmdbEpisodeDetails episode = button.Tag as TmdbEpisodeDetails;
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

        private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/w500";

        private MediaItem BuildEpisodeMediaItem(TmdbEpisodeDetails episode)
        {
            MediaItem episodeItem = new MediaItem
            {
                StremioId = _showImdbId,
                Season = episode.SeasonNumber,
                Episode = episode.EpisodeNumber,
                Type = _show.Type,
                TmdbId = _show.TmdbId,
                Title = _show.Title +
                    (!string.IsNullOrWhiteSpace(episode.Name)
                        ? " - " + episode.Name
                        : " - S" + episode.SeasonNumber + "E" + episode.EpisodeNumber),
                Subtitle = "Season " + episode.SeasonNumber + " \u00B7 Episode " + episode.EpisodeNumber
            };

            if (!string.IsNullOrWhiteSpace(episode.AirDate) && episode.AirDate.Length >= 4)
            {
                episodeItem.Year = episode.AirDate.Substring(0, 4);
            }

            if (episode.VoteAverage > 0)
            {
                episodeItem.Rating = episode.VoteAverage.ToString("0.0");
            }

            if (!string.IsNullOrWhiteSpace(episode.StillPath))
            {
                episodeItem.PosterPath = TmdbImageBaseUrl + episode.StillPath;
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

        // Adapted from the old two-page back behaviour to the new
        // always-both-visible layout: backing out of the episode panel
        // returns focus to the season panel (landing on the currently
        // active season) rather than cancelling outright, matching what
        // "Back from the episode list returns to the season list" meant
        // before this polish removed the page toggle. Backing out of the
        // season panel still cancels the whole browser.
        private void HandleBack()
        {
            if (IsFocusInEpisodePanel() && _seasonButtons.Count > 0)
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
            List<Button> buttons = IsFocusInEpisodePanel() ? _episodeButtons : _seasonButtons;

            MoveWithinList(buttons, direction);
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
                    index = buttons.Count - 1;
                }
                else if (index >= buttons.Count)
                {
                    index = 0;
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
