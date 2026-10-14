using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // LOCAL MEDIA VIEW (Local Files library page)
    // =============================================================
    //
    // The Local Files page: a History-style grid of MediaCards over the
    // persisted local library index (LocalLibraryManager), with a title
    // filter, a movies / TV / unmatched filter, Rescan / Cancel, a Folders
    // panel (Playnite folder picker), a per-file options panel (play,
    // details, fix match, keep unmatched) and a TV show -> episode drill.
    //
    // This view owns presentation only. Scanning, matching, the index and
    // the folder list belong to LocalLibraryManager; playback and the
    // Movie Details / Episode Browser hand-offs are raised as events so
    // StremioVideoCenterView routes them through its existing flows
    // (nothing here plays media or opens another page itself).
    //
    // Same overlay shape as HistoryView/EpisodeBrowserView: Closed event,
    // PreviewKeyDown for Escape, HandleControllerInput(ControllerInput)
    // for the "most specific overlay wins" input chain. No path is ever
    // shown outside the Folders panel (the user's own configuration).
    public partial class LocalMediaView : UserControl
    {
        public event EventHandler Closed;

        // Play this local item now (an explicit user action). The item is
        // the playable MediaItem from LocalLibraryManager.BuildMediaItem.
        public event EventHandler<MediaItemSelectedEventArgs> PlayRequested;

        // Open the existing Movie Details / Episode Browser for a matched
        // item (catalogue-shaped MediaItem).
        public event EventHandler<MediaItemSelectedEventArgs> DetailsRequested;

        private const double ResultsCardWidth = 206;
        private const int FocusRetryLimit = 20;

        private LocalLibraryManager _manager;
        private bool _subscribed;
        private bool _initialised;

        private LocalBrowseFilter _filter = LocalBrowseFilter.All;
        private string _showKey;
        private string _showTitle;

        private List<LocalBrowseEntry> _entries = new List<LocalBrowseEntry>();
        private readonly Dictionary<MediaItem, LocalBrowseEntry> _cardMap =
            new Dictionary<MediaItem, LocalBrowseEntry>();
        private List<MediaItem> _cards = new List<MediaItem>();
        private string _gridSignature = string.Empty;
        private HashSet<string> _offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _offlineCount;
        private int _refreshVersion;
        private bool _refreshQueued;

        private DispatcherTimer _filterTimer;

        private LocalBrowseEntry _optionsEntry;
        private LocalLibraryItem _matchItem;
        private CancellationTokenSource _matchCts;
        private TextBox _keyboardTarget;

        private Action _messageAction;

        // Grid position of the card last opened, restored after playback.
        private int _lastFocusedIndex;

        public LocalMediaView()
        {
            InitializeComponent();

            DragScroll.Attach(PageScrollViewer, DragScrollAxis.Vertical);

            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(ResultCard_Selected));

            // Directional focus stays inside an open panel.
            KeyboardNavigation.SetDirectionalNavigation(FoldersPanel, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetDirectionalNavigation(OptionsPanel, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetDirectionalNavigation(MatchPanel, KeyboardNavigationMode.Contained);

            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _filterTimer.Tick += FilterTimer_Tick;

            Loaded += LocalMediaView_Loaded;
            Unloaded += LocalMediaView_Unloaded;
        }

        // =========================================================
        // INITIALIZE
        // =========================================================

        public void Initialize(LocalLibraryManager manager)
        {
            if (_manager != null && _subscribed)
            {
                _manager.Changed -= Manager_Changed;
                _subscribed = false;
            }

            _manager = manager;

            if (_manager != null && IsLoaded)
            {
                _manager.Changed += Manager_Changed;
                _subscribed = true;
            }

            if (!_initialised)
            {
                _initialised = true;
                BeginInitialLoad();
            }
        }

        private async void BeginInitialLoad()
        {
            if (_manager == null)
            {
                ShowMessage("Local Files is not available.", null, null);
                return;
            }

            ShowMessage("Loading your library...", null, null);

            try
            {
                await _manager.EnsureLoadedAsync();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: the library could not be loaded. " + UserFacingErrors.ForLog(ex));
            }

            // First open of the session: bring the index up to date. Unchanged
            // files cost no network lookups (see LocalLibraryManager).
            if (_manager.IsEnabled &&
                _manager.GetLocations().Count > 0 &&
                _manager.LastScanUtc == default(DateTime) &&
                !_manager.IsBusy)
            {
                _manager.StartScan(false);
            }

            await RefreshAsync(true);
        }

        private void LocalMediaView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_manager != null && !_subscribed)
            {
                _manager.Changed += Manager_Changed;
                _subscribed = true;
                QueueRefresh();
            }

            if (_cards.Count > 0)
            {
                FocusResultCard(0, 0);
            }
            else
            {
                CloseButton.Focus();
            }
        }

        private void LocalMediaView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_manager != null && _subscribed)
            {
                _manager.Changed -= Manager_Changed;
                _subscribed = false;
            }

            CancelMatchSearch();
            _filterTimer.Stop();
        }

        // Called by the host after playback closes etc. so badges (missing
        // files, failed-this-session files) are current.
        public void Refresh()
        {
            QueueRefresh();
        }

        // While the player is up, this page must not hold keyboard focus or
        // take input: disabling it removes every focus target, so focus and
        // input belong to the player (the same outcome as the Source
        // Selector overlay being removed in the streaming flow).
        public void SetPlayerActive(bool active)
        {
            IsEnabled = !active;
        }

        // After the player has closed: back to the grid (or Back button).
        public void FocusAfterPlayback()
        {
            IsEnabled = true;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_cards.Count > 0)
                    {
                        int index = Math.Max(0, Math.Min(_lastFocusedIndex, _cards.Count - 1));
                        FocusResultCard(index, 0);
                    }
                    else
                    {
                        CloseButton.Focus();
                    }
                }),
                DispatcherPriority.Input);
        }

        // =========================================================
        // MANAGER CHANGES
        // =========================================================

        private void Manager_Changed(object sender, EventArgs e)
        {
            QueueRefresh();
        }

        // The manager raises Changed from background threads and during
        // scans; coalesce into one UI refresh at a time.
        private void QueueRefresh()
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;

            Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    _refreshQueued = false;
                    await RefreshAsync(false);
                }),
                DispatcherPriority.Background);
        }

        private async System.Threading.Tasks.Task RefreshAsync(bool focusGrid)
        {
            if (_manager == null)
            {
                return;
            }

            int version = ++_refreshVersion;

            List<LocalFolderStatus> folders = null;

            try
            {
                folders = await _manager.GetFolderStatusesAsync();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: folder states could not be read. " + UserFacingErrors.ForLog(ex));
            }

            if (version != _refreshVersion)
            {
                return;
            }

            var offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (folders != null)
            {
                foreach (LocalFolderStatus folder in folders)
                {
                    if (folder.State != LocalFolderState.Accessible && !string.IsNullOrEmpty(folder.Path))
                    {
                        offline.Add(folder.Path);
                    }
                }
            }

            _offline = offline;
            _offlineCount = offline.Count;

            if (FoldersPanel.Visibility == Visibility.Visible && folders != null)
            {
                BindFolders(folders);
            }

            ApplyState(focusGrid);
        }

        // =========================================================
        // STATE -> UI
        // =========================================================

        private void ApplyState(bool focusGrid)
        {
            LocalLibraryStatus status = _manager.GetStatus();
            bool enabled = _manager.IsEnabled;
            bool busy = status.Phase != LocalScanPhase.Idle;
            int locationCount = _manager.GetLocations().Count;
            List<LocalLibraryItem> items = _manager.GetItemsSnapshot();

            // Toolbar buttons.
            CancelScanButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            RescanButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            RescanButton.IsEnabled = enabled && locationCount > 0;
            FoldersButton.IsEnabled = true;

            int unmatched = items.Count(i => !i.IsMissing && string.IsNullOrEmpty(i.DuplicateOfId) && !i.IsMatched);
            RetryButton.Visibility = (!busy && enabled && unmatched > 0) ? Visibility.Visible : Visibility.Collapsed;

            UpdateStatusPanel(status, busy, enabled);

            // Disabled / no folders / empty.
            if (!enabled)
            {
                SetGrid(new List<LocalBrowseEntry>(), false);
                ShowMessage(
                    "Local Files is turned off. Turn it on in Addon Management to use your local library.",
                    null,
                    null);
                return;
            }

            if (locationCount == 0)
            {
                SetGrid(new List<LocalBrowseEntry>(), false);
                ShowMessage(
                    "No folders added yet. Add a folder that contains your movies and shows.",
                    "Add folder...",
                    OpenFoldersPanel);
                return;
            }

            List<LocalBrowseEntry> entries;

            if (_showKey == null)
            {
                entries = LocalLibraryBrowse.BuildTopLevel(items, _filter, FilterBox.Text, _offline);
            }
            else
            {
                entries = LocalLibraryBrowse.BuildEpisodes(items, _showKey, FilterBox.Text, _offline);

                if (entries.Count == 0 && string.IsNullOrWhiteSpace(FilterBox.Text))
                {
                    // The show disappeared (folder removed / files gone).
                    ExitShow();
                    entries = LocalLibraryBrowse.BuildTopLevel(items, _filter, FilterBox.Text, _offline);
                }
            }

            bool changed = SetGrid(entries, focusGrid);

            if (entries.Count > 0)
            {
                HideMessage();
                return;
            }

            if (busy)
            {
                ShowMessage("Looking for videos...", null, null);
            }
            else if (items.Count(i => !i.IsMissing) == 0)
            {
                ShowMessage(
                    _offlineCount > 0
                        ? "None of your folders can be reached right now. Check that the drive or share is connected, then rescan."
                        : "No videos were found in your folders.",
                    "Rescan",
                    () => StartScan(false));
            }
            else
            {
                ShowMessage("Nothing matches the current filter.", null, null);
            }

            if (changed && focusGrid)
            {
                CloseButton.Focus();
            }
        }

        private void UpdateStatusPanel(LocalLibraryStatus status, bool busy, bool enabled)
        {
            var text = new StringBuilder();
            bool progressVisible = false;
            bool indeterminate = true;

            if (status.Phase == LocalScanPhase.Scanning)
            {
                progressVisible = true;
                text.Append("Scanning folders");

                if (status.Progress != null)
                {
                    if (status.Progress.LocationCount > 1)
                    {
                        text.Append(" (" + Math.Min(status.Progress.LocationIndex + 1, status.Progress.LocationCount) +
                                    " of " + status.Progress.LocationCount + ")");
                    }

                    text.Append(" - " + status.Progress.VideoFilesFound + " video" +
                                (status.Progress.VideoFilesFound == 1 ? string.Empty : "s") + " found");
                }
            }
            else if (status.Phase == LocalScanPhase.Matching)
            {
                progressVisible = true;
                text.Append("Identifying titles");

                if (status.Progress != null && status.Progress.MatchesTotal > 0)
                {
                    indeterminate = false;
                    ScanProgressBar.Maximum = status.Progress.MatchesTotal;
                    ScanProgressBar.Value = Math.Min(status.Progress.MatchesDone, status.Progress.MatchesTotal);
                    text.Append(" - " + status.Progress.MatchesDone + " of " + status.Progress.MatchesTotal);
                }
            }
            else if (!string.IsNullOrEmpty(status.LastMessage))
            {
                text.Append(status.LastMessage);
            }

            if (enabled && _offlineCount > 0 && !busy)
            {
                if (text.Length > 0)
                {
                    text.Append("  ");
                }

                text.Append(_offlineCount + " folder" + (_offlineCount == 1 ? " is" : "s are") +
                            " not available right now.");
            }

            ScanProgressBar.IsIndeterminate = indeterminate;
            ScanProgressBar.Visibility = progressVisible ? Visibility.Visible : Visibility.Collapsed;

            StatusText.Text = text.ToString();
            StatusText.Foreground = status.LastScanFailed
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0xA4, 0x58))
                : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

            StatusPanel.Visibility = text.Length > 0 || progressVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // Replaces the card list only when it actually changed, so a scan's
        // progress ticks do not rebuild the grid or steal focus. Returns
        // true when the list was replaced.
        private bool SetGrid(List<LocalBrowseEntry> entries, bool focusFirst)
        {
            string signature = BuildSignature(entries);

            if (signature == _gridSignature)
            {
                return false;
            }

            int focusedIndex = GetFocusedResultIndex();
            string focusedKey = focusedIndex >= 0 && focusedIndex < _entries.Count
                ? _entries[focusedIndex].Key
                : null;

            _gridSignature = signature;
            _entries = entries;
            _cardMap.Clear();

            var cards = new List<MediaItem>(entries.Count);

            foreach (LocalBrowseEntry entry in entries)
            {
                MediaItem card = ToCard(entry);
                cards.Add(card);
                _cardMap[card] = entry;
            }

            _cards = cards;
            ResultsItemsControl.ItemsSource = cards;
            ResultsItemsControl.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (cards.Count > 0)
            {
                int target = -1;

                if (focusedKey != null)
                {
                    target = entries.FindIndex(e => e.Key == focusedKey);
                }

                if (target >= 0)
                {
                    FocusResultCard(target, 0);
                }
                else if (focusFirst)
                {
                    PageScrollViewer.ScrollToVerticalOffset(0);
                    FocusResultCard(0, 0);
                }
            }

            return true;
        }

        private static string BuildSignature(List<LocalBrowseEntry> entries)
        {
            var builder = new StringBuilder(entries.Count * 24);

            foreach (LocalBrowseEntry entry in entries)
            {
                builder.Append(entry.Key).Append('|')
                       .Append(entry.Title).Append('|')
                       .Append(entry.Detail).Append('|')
                       .Append(entry.Badge).Append('|')
                       .Append(entry.PosterUrl).Append('\n');
            }

            return builder.ToString();
        }

        // Entry -> the MediaItem a MediaCard displays. Year line = detail
        // ("S01E05", "3 episodes", year); rating line = status badge.
        // Never carries a path.
        private static MediaItem ToCard(LocalBrowseEntry entry)
        {
            return new MediaItem
            {
                StremioId = entry.Key,
                Title = entry.Title,
                Year = entry.Detail,
                Rating = string.IsNullOrEmpty(entry.Badge) ? string.Empty : entry.Badge,
                PosterPath = entry.PosterUrl,
                Type = entry.Kind == LocalBrowseEntryKind.Show ? "TV" : "Movie"
            };
        }

        private void ShowMessage(string text, string actionLabel, Action action)
        {
            MessageText.Text = text;
            _messageAction = action;

            if (!string.IsNullOrEmpty(actionLabel) && action != null)
            {
                MessageActionButton.Content = actionLabel;
                MessageActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                MessageActionButton.Visibility = Visibility.Collapsed;
            }

            MessagePanel.Visibility = Visibility.Visible;
        }

        private void HideMessage()
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            _messageAction = null;
        }

        private void MessageActionButton_Click(object sender, RoutedEventArgs e)
        {
            Action action = _messageAction;

            if (action != null)
            {
                action();
            }
        }

        // =========================================================
        // TOOLBAR
        // =========================================================

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_manager == null || !IsLoaded)
            {
                return;
            }

            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void FilterTimer_Tick(object sender, EventArgs e)
        {
            _filterTimer.Stop();
            QueueRefresh();
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender == FilterMoviesButton)
            {
                _filter = LocalBrowseFilter.Movies;
            }
            else if (sender == FilterTvButton)
            {
                _filter = LocalBrowseFilter.Tv;
            }
            else if (sender == FilterUnmatchedButton)
            {
                _filter = LocalBrowseFilter.Unmatched;
            }
            else
            {
                _filter = LocalBrowseFilter.All;
            }

            // Filters apply to the top level; leave any open show.
            if (_showKey != null)
            {
                ExitShow();
            }

            FilterAllButton.Tag = _filter == LocalBrowseFilter.All ? "Active" : null;
            FilterMoviesButton.Tag = _filter == LocalBrowseFilter.Movies ? "Active" : null;
            FilterTvButton.Tag = _filter == LocalBrowseFilter.Tv ? "Active" : null;
            FilterUnmatchedButton.Tag = _filter == LocalBrowseFilter.Unmatched ? "Active" : null;

            QueueRefresh();
        }

        private void RescanButton_Click(object sender, RoutedEventArgs e)
        {
            StartScan(false);
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            StartScan(true);
        }

        private void StartScan(bool retryUnmatched)
        {
            if (_manager != null)
            {
                _manager.StartScan(retryUnmatched);
                QueueRefresh();
            }
        }

        private void CancelScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manager != null)
            {
                _manager.CancelScan();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            EventHandler handler = Closed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // CARD SELECTION
        // =========================================================

        private void ResultCard_Selected(object sender, RoutedEventArgs e)
        {
            MediaCard card = e.OriginalSource as MediaCard;

            if (card == null)
            {
                return;
            }

            MediaItem display = card.DataContext as MediaItem;
            LocalBrowseEntry entry;

            if (display == null || !_cardMap.TryGetValue(display, out entry))
            {
                return;
            }

            e.Handled = true;

            if (entry.Kind == LocalBrowseEntryKind.Show)
            {
                EnterShow(entry);
                return;
            }

            OpenOptions(entry);
        }

        private void EnterShow(LocalBrowseEntry show)
        {
            _showKey = show.ShowKey;
            _showTitle = show.Title;
            TitleText.Text = show.Title;
            FilterBox.Text = string.Empty;
            _gridSignature = string.Empty;
            PageScrollViewer.ScrollToVerticalOffset(0);
            ApplyState(true);
        }

        private void ExitShow()
        {
            _showKey = null;
            _showTitle = null;
            TitleText.Text = "Local Files";
            _gridSignature = string.Empty;
        }

        // =========================================================
        // OPTIONS PANEL
        // =========================================================

        private void OpenOptions(LocalBrowseEntry entry)
        {
            if (entry == null || entry.Item == null)
            {
                return;
            }

            _optionsEntry = entry;
            LocalLibraryItem item = entry.Item;

            int openedIndex = _entries.IndexOf(entry);

            if (openedIndex >= 0)
            {
                _lastFocusedIndex = openedIndex;
            }

            OptionsTitleText.Text = entry.Title;

            var detail = new StringBuilder();

            if (!string.IsNullOrEmpty(entry.Detail))
            {
                detail.Append(entry.Detail);
            }

            if (!string.IsNullOrEmpty(entry.Badge))
            {
                if (detail.Length > 0)
                {
                    detail.Append("  -  ");
                }

                detail.Append(entry.Badge);
            }

            if (item.IsAmbiguous && !string.IsNullOrEmpty(item.AmbiguityReason))
            {
                if (detail.Length > 0)
                {
                    detail.Append("\n");
                }

                detail.Append(item.AmbiguityReason);
            }

            OptionsDetailText.Text = detail.ToString();

            bool busy = _manager.IsBusy;
            bool offline = !string.IsNullOrEmpty(item.LocationPath) && _offline.Contains(item.LocationPath);

            OptionPlayButton.IsEnabled = !item.IsMissing && !offline;
            OptionDetailsButton.Visibility = item.IsMatched ? Visibility.Visible : Visibility.Collapsed;
            OptionFixMatchButton.IsEnabled = !busy;
            OptionKeepUnmatchedButton.Visibility =
                (item.IsMatched || item.MatchStatus == LocalMatchStatus.NeedsReview)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            OptionKeepUnmatchedButton.IsEnabled = !busy;

            OptionsPanel.Visibility = Visibility.Visible;

            if (OptionPlayButton.IsEnabled)
            {
                OptionPlayButton.Focus();
            }
            else
            {
                OptionsCloseButton.Focus();
            }
        }

        private void CloseOptions()
        {
            OptionsPanel.Visibility = Visibility.Collapsed;
            RestoreGridFocus(_optionsEntry);
            _optionsEntry = null;
        }

        private void OptionsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseOptions();
        }

        private void OptionPlayButton_Click(object sender, RoutedEventArgs e)
        {
            LocalBrowseEntry entry = _optionsEntry;

            if (entry == null || entry.Item == null)
            {
                return;
            }

            MediaItem media = _manager.BuildMediaItem(entry.Item);

            OptionsPanel.Visibility = Visibility.Collapsed;
            _optionsEntry = null;

            EventHandler<MediaItemSelectedEventArgs> handler = PlayRequested;

            if (media != null && handler != null)
            {
                handler(this, new MediaItemSelectedEventArgs(media));
            }
        }

        private void OptionDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            LocalBrowseEntry entry = _optionsEntry;

            if (entry == null || entry.Item == null)
            {
                return;
            }

            MediaItem media = _manager.BuildCatalogueItem(entry.Item);

            OptionsPanel.Visibility = Visibility.Collapsed;
            _optionsEntry = null;

            EventHandler<MediaItemSelectedEventArgs> handler = DetailsRequested;

            if (media != null && handler != null)
            {
                handler(this, new MediaItemSelectedEventArgs(media));
            }
        }

        private void OptionKeepUnmatchedButton_Click(object sender, RoutedEventArgs e)
        {
            LocalBrowseEntry entry = _optionsEntry;

            if (entry == null || entry.Item == null)
            {
                return;
            }

            bool done = _manager.KeepUnmatched(entry.Item.Id);

            if (!done)
            {
                OptionsDetailText.Text = "The library is busy. Try again when the scan has finished.";
                return;
            }

            _gridSignature = string.Empty;
            CloseOptions();
            QueueRefresh();
        }

        private void OptionFixMatchButton_Click(object sender, RoutedEventArgs e)
        {
            LocalBrowseEntry entry = _optionsEntry;

            if (entry == null || entry.Item == null)
            {
                return;
            }

            OptionsPanel.Visibility = Visibility.Collapsed;
            OpenMatchPanel(entry.Item);
        }

        // =========================================================
        // FIX MATCH PANEL
        // =========================================================

        internal sealed class CandidateRow
        {
            public string Label { get; set; }
            public string Detail { get; set; }
            public LocalMatchCandidate Candidate { get; set; }
        }

        private void OpenMatchPanel(LocalLibraryItem item)
        {
            _matchItem = item;

            MatchTitleText.Text = item.IsEpisode ? "Fix match for this series" : "Fix match";
            MatchSubtitleText.Text = string.IsNullOrWhiteSpace(item.ParsedTitle)
                ? item.FileName
                : item.ParsedTitle;

            MatchSearchBox.Text = item.ParsedTitle ?? string.Empty;
            MatchCandidatesControl.ItemsSource = null;
            MatchStatusText.Text = "Edit the title if needed, then search.";

            MatchPanel.Visibility = Visibility.Visible;
            MatchSearchBox.Focus();
            MatchSearchBox.CaretIndex = MatchSearchBox.Text.Length;

            RunMatchSearch();
        }

        private void CloseMatchPanel()
        {
            CancelMatchSearch();
            CloseKeyboardIfOpen();
            MatchPanel.Visibility = Visibility.Collapsed;

            LocalLibraryItem item = _matchItem;
            _matchItem = null;

            if (item != null)
            {
                RestoreGridFocus(_optionsEntry ?? _entries.FirstOrDefault(en => en.Item != null && en.Item.Id == item.Id));
            }

            _optionsEntry = null;
        }

        private void CancelMatchSearch()
        {
            CancellationTokenSource cts = _matchCts;
            _matchCts = null;

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void MatchCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseMatchPanel();
        }

        private void MatchSearchButton_Click(object sender, RoutedEventArgs e)
        {
            RunMatchSearch();
        }

        private void MatchSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RunMatchSearch();
                e.Handled = true;
            }
        }

        private async void RunMatchSearch()
        {
            LocalLibraryItem item = _matchItem;
            string text = (MatchSearchBox.Text ?? string.Empty).Trim();

            if (item == null)
            {
                return;
            }

            if (text.Length == 0)
            {
                MatchStatusText.Text = "Type a title to search for.";
                return;
            }

            CancelMatchSearch();

            var cts = new CancellationTokenSource();
            _matchCts = cts;

            MatchStatusText.Text = "Searching...";
            MatchCandidatesControl.ItemsSource = null;

            List<LocalMatchCandidate> results = null;
            bool failed = false;

            try
            {
                results = await _manager.SearchCandidatesAsync(text, item.ParsedYear, item.IsEpisode, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failed = true;
                StremioVideoCenterLogger.Warn("Local Files: a match search failed. " + UserFacingErrors.ForLog(ex));
            }

            if (!ReferenceEquals(_matchCts, cts) || _matchItem == null)
            {
                return;
            }

            if (failed)
            {
                MatchStatusText.Text = "The search could not be completed. Check your connection and try again.";
                return;
            }

            if (results == null || results.Count == 0)
            {
                MatchStatusText.Text = "No results. Try a different title.";
                return;
            }

            MatchStatusText.Text = "Choose the correct title:";

            MatchCandidatesControl.ItemsSource = results
                .Select(c => new CandidateRow
                {
                    Label = string.IsNullOrEmpty(c.Year) ? c.Title : c.Title + " (" + c.Year + ")",
                    Detail = DescribeCandidateType(c),
                    Candidate = c
                })
                .ToList();
        }

        private static string DescribeCandidateType(LocalMatchCandidate candidate)
        {
            if (string.Equals(candidate.Type, "movie", StringComparison.OrdinalIgnoreCase))
            {
                return "Movie";
            }

            if (string.Equals(candidate.Type, "anime", StringComparison.OrdinalIgnoreCase))
            {
                return "Anime";
            }

            return "TV series";
        }

        private void MatchCandidateButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var row = button != null ? button.Tag as CandidateRow : null;
            LocalLibraryItem item = _matchItem;

            if (row == null || item == null)
            {
                return;
            }

            if (!_manager.ApplyUserMatch(item.Id, row.Candidate))
            {
                MatchStatusText.Text = item.IsEpisode == string.Equals(row.Candidate.Type, "movie", StringComparison.OrdinalIgnoreCase)
                    ? "That title does not fit this file (movie vs. series)."
                    : "The library is busy. Try again when the scan has finished.";
                return;
            }

            _gridSignature = string.Empty;
            CloseMatchPanel();
            QueueRefresh();
        }

        private void MatchKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            OpenKeyboard(MatchSearchBox);
        }

        // =========================================================
        // FOLDERS PANEL
        // =========================================================

        internal sealed class FolderRow
        {
            public string Path { get; set; }
            public string StateText { get; set; }
            public Brush StateBrush { get; set; }
        }

        private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0xC8, 0x8A)));
        private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xA4, 0x58)));

        private static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private void FoldersButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFoldersPanel();
        }

        private async void OpenFoldersPanel()
        {
            FoldersNoticeText.Visibility = Visibility.Collapsed;
            AddFolderWindowsButton.Visibility =
                _manager != null && _manager.CanPickFolder ? Visibility.Visible : Visibility.Collapsed;
            FoldersPanel.Visibility = Visibility.Visible;
            AddFolderButton.Focus();

            await ReloadFoldersAsync();
        }

        private async System.Threading.Tasks.Task ReloadFoldersAsync()
        {
            List<LocalFolderStatus> folders = null;

            try
            {
                folders = await _manager.GetFolderStatusesAsync();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn("Local Files: folder states could not be read. " + UserFacingErrors.ForLog(ex));
            }

            if (folders != null)
            {
                BindFolders(folders);
            }
        }

        private void BindFolders(List<LocalFolderStatus> folders)
        {
            FolderItemsControl.ItemsSource = folders
                .Select(f => new FolderRow
                {
                    Path = f.Path,
                    StateText = DescribeFolder(f),
                    StateBrush = f.State == LocalFolderState.Accessible ? OkBrush : WarnBrush
                })
                .ToList();

            FoldersEmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string DescribeFolder(LocalFolderStatus folder)
        {
            switch (folder.State)
            {
                case LocalFolderState.Missing:
                    return "Folder not found";

                case LocalFolderState.Unavailable:
                    return "Not available right now (drive or share disconnected?)";

                default:
                    return folder.ItemCount + " video" + (folder.ItemCount == 1 ? string.Empty : "s");
            }
        }

        private void ShowFoldersNotice(string text)
        {
            FoldersNoticeText.Text = text;
            FoldersNoticeText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // "Add folder..." opens the in-app folder browser (controller-friendly).
        // Nothing is added until a folder is chosen there; cancelling adds
        // nothing.
        private void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            ShowFoldersNotice(null);
            FolderBrowser.Open();
        }

        // Fallback: the standard Windows dialog, kept for network shares that
        // are not reachable from the drive list (e.g. an unmapped UNC path).
        private async void AddFolderWindowsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_manager == null)
            {
                return;
            }

            if (!_manager.CanPickFolder)
            {
                ShowFoldersNotice("The folder picker is not available.");
                return;
            }

            string picked = _manager.PickFolder();

            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            await ApplyPickedFolderAsync(picked);
        }

        private async void FolderBrowser_FolderChosen(object sender, string path)
        {
            FolderBrowser.Close();
            AddFolderButton.Focus();

            await ApplyPickedFolderAsync(path);
        }

        private void FolderBrowser_Cancelled(object sender, EventArgs e)
        {
            FolderBrowser.Close();
            AddFolderButton.Focus();
        }

        // One path for both pickers: the existing manager/service add the
        // folder (normalise, de-duplicate, overlap check, persist) and a
        // scan starts exactly as before.
        private async System.Threading.Tasks.Task ApplyPickedFolderAsync(string picked)
        {
            bool busy;
            AddLocationResult result = _manager.AddFolder(picked, out busy);

            if (busy)
            {
                ShowFoldersNotice("A scan is running. Cancel it or wait for it to finish before changing folders.");
                return;
            }

            switch (result)
            {
                case AddLocationResult.Added:
                    ShowFoldersNotice(null);
                    await ReloadFoldersAsync();

                    // New folder: index it straight away.
                    _manager.StartScan(false);
                    break;

                case AddLocationResult.Duplicate:
                    ShowFoldersNotice("That folder is already in the list.");
                    break;

                case AddLocationResult.Overlaps:
                    ShowFoldersNotice("That folder overlaps one that is already added.");
                    break;

                case AddLocationResult.NotAccessible:
                    ShowFoldersNotice("That folder cannot be reached right now.");
                    break;

                default:
                    ShowFoldersNotice("That is not a usable folder.");
                    break;
            }

            AddFolderButton.Focus();
        }

        private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string path = button != null ? button.Tag as string : null;

            if (string.IsNullOrEmpty(path) || _manager == null)
            {
                return;
            }

            bool busy;
            bool removed = _manager.RemoveFolder(path, out busy);

            if (busy)
            {
                ShowFoldersNotice("A scan is running. Cancel it or wait for it to finish before changing folders.");
                return;
            }

            if (!removed)
            {
                ShowFoldersNotice("That folder could not be removed.");
                return;
            }

            ShowFoldersNotice(null);
            _gridSignature = string.Empty;
            await ReloadFoldersAsync();
            AddFolderButton.Focus();
        }

        private void FoldersDoneButton_Click(object sender, RoutedEventArgs e)
        {
            CloseFoldersPanel();
        }

        private void CloseFoldersPanel()
        {
            FoldersPanel.Visibility = Visibility.Collapsed;
            QueueRefresh();

            if (_cards.Count > 0)
            {
                FocusResultCard(0, 0);
            }
            else
            {
                FoldersButton.Focus();
            }
        }

        // =========================================================
        // ON-SCREEN KEYBOARD
        // =========================================================

        private void OpenKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            OpenKeyboard(FilterBox);
        }

        private void OpenKeyboard(TextBox target)
        {
            _keyboardTarget = target;
            KeyboardOverlay.Visibility = Visibility.Visible;
            KeyboardOverlay.FocusFirstKey();
        }

        private void CloseKeyboardIfOpen()
        {
            if (_keyboardTarget != null)
            {
                CloseKeyboard();
            }
        }

        private void CloseKeyboard()
        {
            TextBox target = _keyboardTarget;
            _keyboardTarget = null;
            KeyboardOverlay.Visibility = Visibility.Collapsed;

            if (target != null)
            {
                target.Focus();
                Keyboard.Focus(target);
            }
        }

        private void KeyboardOverlay_CharacterEntered(object sender, string e)
        {
            TextBox target = _keyboardTarget;

            if (target == null || string.IsNullOrEmpty(e))
            {
                return;
            }

            string current = target.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(target.CaretIndex, current.Length));

            target.Text = current.Insert(caret, e);
            target.CaretIndex = caret + e.Length;
        }

        private void KeyboardOverlay_BackspaceRequested(object sender, EventArgs e)
        {
            TextBox target = _keyboardTarget;

            if (target == null)
            {
                return;
            }

            string current = target.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(target.CaretIndex, current.Length));

            if (caret == 0)
            {
                return;
            }

            target.Text = current.Remove(caret - 1, 1);
            target.CaretIndex = caret - 1;
        }

        private void KeyboardOverlay_ClearRequested(object sender, EventArgs e)
        {
            TextBox target = _keyboardTarget;

            if (target != null)
            {
                target.Text = string.Empty;
                target.CaretIndex = 0;
            }
        }

        private void KeyboardOverlay_SubmitRequested(object sender, EventArgs e)
        {
            TextBox target = _keyboardTarget;
            CloseKeyboard();

            if (target == MatchSearchBox)
            {
                RunMatchSearch();
            }
        }

        private void KeyboardOverlay_CloseRequested(object sender, EventArgs e)
        {
            CloseKeyboard();
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void LocalMediaView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // While the on-screen keyboard is open it owns input.
            if (_keyboardTarget != null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (GoBackOneLevel())
                {
                    e.Handled = true;
                }
            }
        }

        // Closes the top-most layer; raises Closed when nothing is left.
        // Returns true (always handled).
        private bool GoBackOneLevel()
        {
            if (_keyboardTarget != null)
            {
                CloseKeyboard();
                return true;
            }

            if (FolderBrowser.IsOpen)
            {
                // Cancel: never adds a folder.
                FolderBrowser_Cancelled(this, EventArgs.Empty);
                return true;
            }

            if (MatchPanel.Visibility == Visibility.Visible)
            {
                CloseMatchPanel();
                return true;
            }

            if (OptionsPanel.Visibility == Visibility.Visible)
            {
                CloseOptions();
                return true;
            }

            if (FoldersPanel.Visibility == Visibility.Visible)
            {
                CloseFoldersPanel();
                return true;
            }

            if (_showKey != null)
            {
                string leaving = _showKey;
                ExitShow();
                FilterBox.Text = string.Empty;
                ApplyState(false);
                FocusShowCard(leaving);
                return true;
            }

            RaiseClosed();
            return true;
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            if (_keyboardTarget != null)
            {
                return KeyboardOverlay.HandleControllerInput(button);
            }

            // The folder browser owns input while it is open (A/B/D-pad).
            if (FolderBrowser.IsOpen)
            {
                return FolderBrowser.HandleControllerInput(button);
            }

            if (button == ControllerInput.B)
            {
                return GoBackOneLevel();
            }

            bool panelOpen = MatchPanel.Visibility == Visibility.Visible ||
                             OptionsPanel.Visibility == Visibility.Visible ||
                             FoldersPanel.Visibility == Visibility.Visible;

            IInputElement focused = Keyboard.FocusedElement;
            var textBox = focused as TextBox;

            if (button == ControllerInput.A)
            {
                if (textBox != null)
                {
                    OpenKeyboard(textBox);
                    return true;
                }

                if (focused is MediaCard)
                {
                    ((MediaCard)focused).RaiseEvent(new RoutedEventArgs(MediaCard.SelectedEvent));
                    return true;
                }

                if (focused is ButtonBase)
                {
                    ((ButtonBase)focused).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    return true;
                }

                return false;
            }

            FocusNavigationDirection direction;

            switch (button)
            {
                case ControllerInput.DPadUp:
                    direction = FocusNavigationDirection.Up;
                    break;

                case ControllerInput.DPadDown:
                    direction = FocusNavigationDirection.Down;
                    break;

                case ControllerInput.DPadLeft:
                    direction = FocusNavigationDirection.Left;
                    break;

                case ControllerInput.DPadRight:
                    direction = FocusNavigationDirection.Right;
                    break;

                default:
                    return false;
            }

            if (textBox != null &&
                (direction == FocusNavigationDirection.Left || direction == FocusNavigationDirection.Right))
            {
                int length = textBox.Text != null ? textBox.Text.Length : 0;

                if (direction == FocusNavigationDirection.Left && textBox.CaretIndex > 0)
                {
                    textBox.CaretIndex--;
                    return true;
                }

                if (direction == FocusNavigationDirection.Right && textBox.CaretIndex < length)
                {
                    textBox.CaretIndex++;
                    return true;
                }
            }

            if (panelOpen)
            {
                var element = focused as UIElement;

                if (element != null)
                {
                    element.MoveFocus(new TraversalRequest(direction));
                }

                return true;
            }

            return NavigatePage(direction);
        }

        // Page-level navigation: Back button row, toolbar row, card grid.
        private bool NavigatePage(FocusNavigationDirection direction)
        {
            int cardIndex = GetFocusedResultIndex();

            if (cardIndex >= 0)
            {
                switch (direction)
                {
                    case FocusNavigationDirection.Left:
                        MoveResultsGrid(-1, 0);
                        break;

                    case FocusNavigationDirection.Right:
                        MoveResultsGrid(1, 0);
                        break;

                    case FocusNavigationDirection.Down:
                        MoveResultsGrid(0, 1);
                        break;

                    case FocusNavigationDirection.Up:
                        if (cardIndex < GetResultsColumnsPerRow())
                        {
                            PageScrollViewer.ScrollToVerticalOffset(0);
                            FilterBox.Focus();
                        }
                        else
                        {
                            MoveResultsGrid(0, -1);
                        }

                        break;
                }

                return true;
            }

            IInputElement focused = Keyboard.FocusedElement;

            if (ReferenceEquals(focused, CloseButton))
            {
                if (direction == FocusNavigationDirection.Down)
                {
                    FilterBox.Focus();
                }

                return true;
            }

            // Toolbar row.
            if (direction == FocusNavigationDirection.Up)
            {
                CloseButton.Focus();
                return true;
            }

            if (direction == FocusNavigationDirection.Down)
            {
                if (_cards.Count > 0)
                {
                    FocusResultCard(0, 0);
                }

                return true;
            }

            var current = focused as UIElement;

            if (current != null)
            {
                current.MoveFocus(new TraversalRequest(direction));
            }
            else
            {
                CloseButton.Focus();
            }

            return true;
        }

        // =========================================================
        // GRID FOCUS (same shape as HistoryView)
        // =========================================================

        private int GetResultsColumnsPerRow()
        {
            double width = ResultsItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            return Math.Max(1, (int)(width / ResultsCardWidth));
        }

        private void MoveResultsGrid(int deltaColumns, int deltaRows)
        {
            int count = _cards.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedResultIndex();

            if (current < 0)
            {
                FocusResultCard(0, 0);
                return;
            }

            int target = current + deltaColumns + (deltaRows * GetResultsColumnsPerRow());
            target = Math.Max(0, Math.Min(target, count - 1));

            FocusResultCard(target, 0);
        }

        private int GetFocusedResultIndex()
        {
            for (int i = 0; i < ResultsItemsControl.Items.Count; i++)
            {
                ContentPresenter presenter =
                    ResultsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

                if (presenter == null)
                {
                    continue;
                }

                MediaCard card = FindVisualChild<MediaCard>(presenter);

                if (card != null && card.IsKeyboardFocusWithin)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusResultCard(int index, int attempt)
        {
            if (index < 0 || index >= ResultsItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                ResultsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                if (attempt < FocusRetryLimit)
                {
                    Dispatcher.BeginInvoke(
                        new Action(() => FocusResultCard(index, attempt + 1)),
                        DispatcherPriority.Loaded);
                }

                return;
            }

            MediaCard card = FindVisualChild<MediaCard>(presenter);

            if (card == null)
            {
                return;
            }

            card.Focus();
            card.BringIntoView();
        }

        private void RestoreGridFocus(LocalBrowseEntry entry)
        {
            if (entry == null)
            {
                if (_cards.Count > 0)
                {
                    FocusResultCard(0, 0);
                }

                return;
            }

            int index = _entries.FindIndex(en => en.Key == entry.Key);

            if (index >= 0)
            {
                FocusResultCard(index, 0);
            }
            else if (_cards.Count > 0)
            {
                FocusResultCard(0, 0);
            }
        }

        private void FocusShowCard(string showKey)
        {
            int index = _entries.FindIndex(en => en.Kind == LocalBrowseEntryKind.Show && en.ShowKey == showKey);
            FocusResultCard(index >= 0 ? index : 0, 0);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                T result = child as T;

                if (result != null)
                {
                    return result;
                }

                result = FindVisualChild<T>(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
