using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // SEARCH VIEW (Slice 15 - global search)
    // =============================================================
    //
    // Searches across Movies, TV Shows, and Anime using only verified,
    // real provider search capabilities - no custom scraper and no
    // second/duplicate search engine of this project's own:
    //
    //   - Cinemeta's own "search" extra on movie/top - live-verified
    //     this slice, ranked and functional.
    //   - Cinemeta's own "search" extra on series/top - independently
    //     live-verified this slice (previously only assumed "by
    //     symmetry" with movie/top - see CatalogueService/Cinemeta_
    //     Capability_Audit.md).
    //   - Kitsu's own "filter[text]" search on its anime collection
    //     (KitsuService.SearchAnimeAsync) - Slice 15 (Anime Architecture
    //     Audit + Kitsu Migration + Provider Role Correction) replaces
    //     the earlier AnimeFLV-backed anime search with this, because
    //     AnimeFLV's own native ids could never be resolved by a
    //     StreamProvider addon (Torrentio/Comet understand "tt" and
    //     "kitsu" ids, not AnimeFLV's own catalogue-slug ids) - see
    //     KitsuService's own remarks for the id convention this now
    //     produces.
    //
    // All three are queried concurrently on submit and shown as up to
    // three separate MediaSections (Movies/TV Shows/Anime), each only
    // visible when it actually has results - never a fabricated empty
    // row. No pagination on search results this slice (unlike the
    // dedicated Movies/TV/Anime pages) - kept deliberately simple,
    // matching this task's own "small, maintainable changes" guidance;
    // Cinemeta's search extra does support skip if a future slice wants
    // to add it.
    //
    // Same overlay conventions as every other overlay in this codebase -
    // Closed/MediaSelected events bridged into an awaitable Task by the
    // caller, PreviewKeyDown for keyboard, HandleControllerInput for the
    // shared "most specific overlay wins" input chain.
    public partial class SearchView : UserControl
    {
        private const int SearchResultTake = 25;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;

        private CinemetaService _cinemetaService;
        private KitsuService _kitsuService;

        private readonly ObservableCollection<MediaItem> _movieItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _tvItems = new ObservableCollection<MediaItem>();
        private readonly ObservableCollection<MediaItem> _animeItems = new ObservableCollection<MediaItem>();

        // Guards against an older, slower search's results landing after
        // a newer one has already started - the same stale-response
        // guard used by CatalogueBrowseView/AnimeBrowseView's pagination.
        private int _searchToken;

        // On-screen keyboard task: same "_active*" convention the top-level
        // StremioVideoCenterView uses for its own overlays (_activeEpisode
        // Browser/_activeSourceSelector/etc.) - non-null exactly while the
        // keyboard overlay is open, and the single thing both PreviewKeyDown
        // and HandleControllerInput check first so the keyboard gets every
        // input while it's up, the same "most specific overlay wins" rule
        // already established one level up. There is only ever one
        // instance (KeyboardOverlay, declared in XAML) - this field tracks
        // whether it is currently the active input target, it does not own
        // the control's lifetime the way the top-level fields own their
        // dynamically created/destroyed pages.
        private OnScreenKeyboardView _activeKeyboard;

        public SearchView()
        {
            InitializeComponent();

            // Mouse click-and-drag scrolling (Slice 20 Stage 3).
            DragScroll.Attach(ResultsScrollViewer, DragScrollAxis.Vertical);

            MoviesSection.MediaSelected += Section_MediaSelected;
            TvSection.MediaSelected += Section_MediaSelected;
            AnimeSection.MediaSelected += Section_MediaSelected;
        }

        // =========================================================
        // INITIALIZE
        // =========================================================

        public void Initialize(CinemetaService cinemetaService, KitsuService kitsuService)
        {
            if (cinemetaService == null)
            {
                throw new ArgumentNullException(nameof(cinemetaService));
            }

            if (kitsuService == null)
            {
                throw new ArgumentNullException(nameof(kitsuService));
            }

            _cinemetaService = cinemetaService;
            _kitsuService = kitsuService;

            MoviesSection.Items = _movieItems;
            TvSection.Items = _tvItems;
            AnimeSection.Items = _animeItems;

            ShowPromptState();
        }

        public void FocusSearchBox()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }), DispatcherPriority.Input);
        }

        // =========================================================
        // ON-SCREEN KEYBOARD
        // =========================================================
        //
        // KeyboardOverlay (Controls/OnScreenKeyboardView) knows nothing
        // about SearchBox - it only raises events for what the user did.
        // This is the one place that turns those events into actual
        // SearchBox.Text/CaretIndex changes, so a future second caller of
        // OnScreenKeyboardView (a different text field entirely) would
        // wire its own equivalent of these handlers rather than this view
        // needing to change.

        private void OpenKeyboard()
        {
            if (_activeKeyboard != null)
            {
                return;
            }

            _activeKeyboard = KeyboardOverlay;
            KeyboardOverlay.Visibility = Visibility.Visible;
            KeyboardOverlay.FocusFirstKey();
        }

        // Single close path for every way the keyboard can go away
        // (Close/X, B/Escape, or after Search submits) - always restores
        // focus to the search box (reusing the same FocusSearchBox() the
        // page itself uses on open) so focus is never left on a control
        // that just became invisible.
        private void CloseKeyboard()
        {
            if (_activeKeyboard == null)
            {
                return;
            }

            _activeKeyboard = null;
            KeyboardOverlay.Visibility = Visibility.Collapsed;
            FocusSearchBox();
        }

        private void OpenKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            OpenKeyboard();
        }

        private void KeyboardOverlay_CharacterEntered(object sender, string e)
        {
            InsertIntoSearchBox(e);
        }

        private void KeyboardOverlay_BackspaceRequested(object sender, EventArgs e)
        {
            RemoveCharacterBeforeCaret();
        }

        private void KeyboardOverlay_ClearRequested(object sender, EventArgs e)
        {
            ClearSearchBox();
        }

        private async void KeyboardOverlay_SubmitRequested(object sender, EventArgs e)
        {
            CloseKeyboard();
            await SubmitSearchAsync();
        }

        private void KeyboardOverlay_CloseRequested(object sender, EventArgs e)
        {
            CloseKeyboard();
        }

        // Inserts/removes at the actual caret position (not always the
        // string's end) so the on-screen keyboard behaves sensibly even
        // when a physical keyboard or mouse click has moved the caret
        // somewhere in the middle of existing text - normal WPF TextBox
        // semantics, not a simplified append-only version of them.
        private void InsertIntoSearchBox(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string current = SearchBox.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(SearchBox.CaretIndex, current.Length));

            SearchBox.Text = current.Insert(caret, text);
            SearchBox.CaretIndex = caret + text.Length;
        }

        private void RemoveCharacterBeforeCaret()
        {
            string current = SearchBox.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(SearchBox.CaretIndex, current.Length));

            if (caret <= 0 || current.Length == 0)
            {
                return;
            }

            SearchBox.Text = current.Remove(caret - 1, 1);
            SearchBox.CaretIndex = caret - 1;
        }

        private void ClearSearchBox()
        {
            SearchBox.Text = string.Empty;
            SearchBox.CaretIndex = 0;
        }

        // =========================================================
        // SEARCH SUBMISSION
        // =========================================================

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SubmitSearchAsync();
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitSearchAsync();
            }
        }

        // Slice 19A: outcome of one provider's search. Failed distinguishes
        // "the request failed" from "the provider answered with no matches"
        // (Failed == false, Items empty) - the two were previously both an
        // empty list.
        private sealed class ProviderSearchResult
        {
            public string Label;
            public List<MediaItem> Items = new List<MediaItem>();
            public bool Failed;
        }

        private async Task SubmitSearchAsync()
        {
            string query = SearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            int token = ++_searchToken;

            ShowLoadingState();

            Task<ProviderSearchResult> moviesTask = SafeSearchCinemetaAsync("movie", query, "Movie", "Movies", token);
            Task<ProviderSearchResult> tvTask = SafeSearchCinemetaAsync("series", query, "TV", "TV Shows", token);
            Task<ProviderSearchResult> animeTask = SafeSearchKitsuAsync(query, token);

            await Task.WhenAll(moviesTask, tvTask, animeTask);

            if (token != _searchToken)
            {
                // A newer search superseded this one while it was still
                // in flight - its results are stale and are discarded.
                return;
            }

            ProviderSearchResult moviesResult = moviesTask.Result;
            ProviderSearchResult tvResult = tvTask.Result;
            ProviderSearchResult animeResult = animeTask.Result;

            List<MediaItem> movies = moviesResult.Items;
            List<MediaItem> tv = tvResult.Items;
            List<MediaItem> anime = animeResult.Items;

            ReplaceCollection(_movieItems, movies);
            ReplaceCollection(_tvItems, tv);
            ReplaceCollection(_animeItems, anime);

            MoviesSection.Visibility = movies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TvSection.Visibility = tv.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AnimeSection.Visibility = anime.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            bool anyResults = movies.Count > 0 || tv.Count > 0 || anime.Count > 0;

            List<string> failedLabels = new List<ProviderSearchResult> { moviesResult, tvResult, animeResult }
                .Where(result => result.Failed)
                .Select(result => result.Label)
                .ToList();

            if (anyResults)
            {
                // Results are shown exactly as before; if some (not all)
                // providers failed, say so rather than implying the
                // missing rows simply had no matches.
                if (failedLabels.Count > 0)
                {
                    PartialFailureText.Text =
                        "Some results could not be loaded (" + string.Join(", ", failedLabels) + "). Try searching again.";
                    PartialFailureText.Visibility = Visibility.Visible;
                }
                else
                {
                    PartialFailureText.Visibility = Visibility.Collapsed;
                }

                ShowResultsState();

                Dispatcher.BeginInvoke(new Action(FocusFirstAvailableSection), DispatcherPriority.Input);
            }
            else if (failedLabels.Count > 0)
            {
                // Nothing to show AND at least one request failed: this is
                // not a confirmed "no matches".
                PartialFailureText.Visibility = Visibility.Collapsed;

                ErrorText.Text = failedLabels.Count == 3
                    ? "Search is unavailable right now. Check your connection and try again."
                    : "Search could not be completed for " + string.Join(", ", failedLabels) +
                      ". No matches were found in the rest. Try again.";

                ShowErrorState();
            }
            else
            {
                PartialFailureText.Visibility = Visibility.Collapsed;
                ShowEmptyState();
            }
        }

        private async Task<ProviderSearchResult> SafeSearchCinemetaAsync(
            string cinemetaType,
            string query,
            string mediaItemType,
            string label,
            int token)
        {
            var result = new ProviderSearchResult { Label = label };
            string errorType = null;

            try
            {
                // Strict variant: a failed request throws (and is reported
                // as Failed below) instead of looking like zero matches.
                List<CinemetaCatalogItem> items =
                    await _cinemetaService.SearchCatalogAsync(cinemetaType, query);

                result.Items = MapCinemetaItems(items, mediaItemType);
            }
            catch (Exception ex)
            {
                result.Failed = true;
                errorType = ex.GetType().Name;
            }

            LogProviderOutcome(token, query, result, errorType);
            return result;
        }

        private async Task<ProviderSearchResult> SafeSearchKitsuAsync(string query, int token)
        {
            var result = new ProviderSearchResult { Label = "Anime" };
            string errorType = null;

            try
            {
                List<KitsuAnimeResource> items = await _kitsuService.SearchAnimeOrThrowAsync(query);

                result.Items = (items ?? new List<KitsuAnimeResource>())
                    .Select(MapKitsuItem)
                    .Where(item => item != null)
                    .Take(SearchResultTake)
                    .ToList();
            }
            catch (Exception ex)
            {
                result.Failed = true;
                errorType = ex.GetType().Name;
            }

            LogProviderOutcome(token, query, result, errorType);
            return result;
        }

        // Slice 19A diagnostic: one line per provider per search. Logs the
        // query LENGTH (not the text), the outcome, the result count and the
        // first few returned titles, so a "why were these results shown"
        // question can be answered from the log. No URLs, tokens or
        // exception messages are logged - only the exception type name.
        private static void LogProviderOutcome(
            int token,
            string query,
            ProviderSearchResult result,
            string errorType)
        {
            string topTitles = string.Join(
                " | ",
                result.Items.Take(3).Select(item => item.Title));

            StremioVideoCenterLogger.Info(
                string.Format(
                    "[SEARCH] token={0} provider={1} queryLength={2} outcome={3} count={4} error={5} top=[{6}]",
                    token,
                    result.Label,
                    query != null ? query.Length : 0,
                    result.Failed ? "Failed" : "Success",
                    result.Items.Count,
                    errorType ?? "none",
                    topTitles));
        }

        // Mirrors CatalogueService.BuildMediaItemsFromCinemeta's own
        // field mapping (a real, already-established shape - not a new
        // one invented for search) - duplicated rather than shared since
        // that method is private to CatalogueService and this is a
        // different, search-specific caller with its own "type" per
        // Cinemeta catalog queried (movie vs. series).
        private static List<MediaItem> MapCinemetaItems(List<CinemetaCatalogItem> items, string mediaItemType)
        {
            var result = new List<MediaItem>();

            if (items == null)
            {
                return result;
            }

            foreach (CinemetaCatalogItem item in items)
            {
                if (result.Count >= SearchResultTake)
                {
                    break;
                }

                if (item == null)
                {
                    continue;
                }

                string imdbId = !string.IsNullOrWhiteSpace(item.ImdbId) ? item.ImdbId : item.Id;

                if (string.IsNullOrWhiteSpace(imdbId) ||
                    !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Poster))
                {
                    continue;
                }

                result.Add(new MediaItem
                {
                    StremioId = imdbId,
                    Title = item.Name,
                    Overview = item.Description,
                    Year = !string.IsNullOrWhiteSpace(item.Year) ? item.Year : item.ReleaseInfo,
                    ReleaseDate = item.Released,
                    Rating = item.ImdbRating,
                    Type = mediaItemType,
                    Genres = item.Genres ?? new List<string>(),
                    PosterPath = item.Poster,
                    BackdropUrl = string.Empty
                });
            }

            return result;
        }

        // Same mapping AnimeBrowseView.MapKitsuToMediaItem uses -
        // duplicated per this project's existing per-view convention
        // rather than shared (see that method's own remarks on
        // StremioId's "kitsu:<id>" shape).
        private static MediaItem MapKitsuItem(KitsuAnimeResource item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.Attributes == null)
            {
                return null;
            }

            string title = !string.IsNullOrWhiteSpace(item.Attributes.CanonicalTitle)
                ? item.Attributes.CanonicalTitle
                : item.Attributes.Titles?.En;

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string poster = item.Attributes.PosterImage?.Large ?? item.Attributes.PosterImage?.Medium;

            var mediaItem = new MediaItem
            {
                StremioId = "kitsu:" + item.Id,
                Title = title,
                Overview = item.Attributes.Synopsis,
                ReleaseDate = item.Attributes.StartDate,
                Rating = item.Attributes.AverageRating,
                Type = "Anime",
                Genres = item.Attributes.Categories?.Select(c => c.Title).ToList() ?? new List<string>(),
                PosterPath = poster ?? string.Empty,
                BackdropUrl = item.Attributes.CoverImage?.Large ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(item.Attributes.StartDate) && item.Attributes.StartDate.Length >= 4)
            {
                mediaItem.Year = item.Attributes.StartDate.Substring(0, 4);
            }

            return mediaItem;
        }

        private static void ReplaceCollection(ObservableCollection<MediaItem> target, List<MediaItem> source)
        {
            target.Clear();

            if (source == null)
            {
                return;
            }

            foreach (MediaItem item in source)
            {
                target.Add(item);
            }
        }

        private void Section_MediaSelected(object sender, MediaItemSelectedEventArgs e)
        {
            RaiseMediaSelected(e.Item);
        }

        // =========================================================
        // VIEW STATES
        // =========================================================

        private void ShowPromptState()
        {
            PromptText.Visibility = Visibility.Visible;
            EmptyText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        private void ShowLoadingState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        private void ShowResultsState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Visible;
        }

        private void ShowEmptyState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        // Slice 19A: a request failed and there is nothing to show - distinct
        // from ShowEmptyState (a successful search with no matches).
        private void ShowErrorState()
        {
            PromptText.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
            LoadingText.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
        }

        // =========================================================
        // SELECTION / CLOSE
        // =========================================================

        private void RaiseMediaSelected(MediaItem item)
        {
            EventHandler<MediaItemSelectedEventArgs> handler = MediaSelected;

            if (handler != null)
            {
                handler(this, new MediaItemSelectedEventArgs(item));
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
        // KEYBOARD INPUT
        // =========================================================

        private void SearchView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // On-screen keyboard task: while the keyboard overlay is open,
            // it owns all input - let this tunneling event continue down
            // to its own PreviewKeyDown handler (OnScreenKeyboardView_
            // PreviewKeyDown) instead of this view's Escape-closes-the-
            // whole-page/section-navigation handling below claiming it
            // first. Same early-return shape as the SearchBox check just
            // below, one level up.
            if (_activeKeyboard != null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                RaiseClosed();
                e.Handled = true;
                return;
            }

            // Search-bar horizontal navigation task: the search box still
            // needs its own text-entry keys (Left/Right for caret
            // movement, Space, digits, letters, Backspace, Enter-to-
            // submit via SearchBox_KeyDown, etc.) to reach the TextBox
            // normally. The one deliberate exception is Right pressed
            // with the caret already at the end of the existing text -
            // there is nowhere further right for the caret to go anyway,
            // so that specific press now moves focus out to the on-
            // screen-keyboard button instead of being a silent no-op.
            // Left is never intercepted here: SearchBox is the leftmost
            // stop in this row, so Left always stays caret movement.
            if (SearchBox.IsKeyboardFocusWithin)
            {
                if (e.Key == Key.Right &&
                    SearchBox.CaretIndex >= (SearchBox.Text?.Length ?? 0))
                {
                    MoveHorizontal(1);
                    e.Handled = true;
                }

                return;
            }

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
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            // On-screen keyboard task: mirrors the guard at the top of
            // SearchView_PreviewKeyDown - the keyboard overlay is the most
            // specific active thing while it's open, so it gets first (and
            // exclusive) claim on every button, the same "most specific
            // overlay wins" rule StremioVideoCenterView.RouteButtonToActive
            // OverlayOrHome already applies one level up.
            if (_activeKeyboard != null)
            {
                return _activeKeyboard.HandleControllerInput(button);
            }

            switch (button)
            {
                case ControllerInput.B:
                    RaiseClosed();
                    return true;

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
                    return ActivateFocusedElement();
            }

            return false;
        }

        // Dedicated-page Back navigation task: lets controller A activate
        // the Back button when it has focus - see CatalogueBrowseView's
        // identical remark on its own ActivateFocusedElement for why this
        // page's own HandleControllerInput (not the generic keyboard
        // Enter/Space path) needs this.
        //
        // On-screen keyboard task: this is also the controller "Activate
        // search input / keyboard" step from the expected flow - the
        // search box already has focus the moment this page opens
        // (FocusSearchBox() in ShowSearchPageAsync), so a controller user
        // never had any way to type into it before. A physical keyboard
        // needs no such step (it already just types into the focused
        // TextBox), so this only applies to the controller path.
        private bool ActivateFocusedElement()
        {
            if (SearchBox.IsKeyboardFocusWithin)
            {
                OpenKeyboard();
                return true;
            }

            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return ActivateFocusedCard();
        }

        private bool ActivateFocusedCard()
        {
            MediaCard card = FindFocusedMediaCard(this);

            if (card == null)
            {
                return false;
            }

            card.RaiseEvent(new RoutedEventArgs(MediaCard.SelectedEvent));

            return true;
        }

        private static MediaCard FindFocusedMediaCard(DependencyObject parent)
        {
            if (parent == null)
            {
                return null;
            }

            MediaCard card = parent as MediaCard;

            if (card != null && card.IsKeyboardFocusWithin)
            {
                return card;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                MediaCard result = FindFocusedMediaCard(child);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        // =========================================================
        // FOCUS MOVEMENT (three stacked MediaSections - same shape as
        // CatalogueBrowseView's default view)
        // =========================================================

        private MediaSection[] GetVisibleSections()
        {
            return new[] { MoviesSection, TvSection, AnimeSection }
                .Where(section => section.Visibility == Visibility.Visible)
                .ToArray();
        }

        private MediaSection GetFocusedSection()
        {
            foreach (MediaSection section in GetVisibleSections())
            {
                if (section.IsKeyboardFocusWithin)
                {
                    return section;
                }
            }

            return null;
        }

        private void MoveVertical(int direction)
        {
            // Dedicated-page Back navigation task: same top-layer <->
            // content chaining as CatalogueBrowseView/AnimeBrowseView -
            // see those files' identical remark on their own MoveVertical.
            //
            // Search results Up-navigation fix: a second stop was added
            // between the Back button and the results - the search
            // header row (SearchBox/OpenKeyboardButton/SearchButton).
            // The chain is now Back <-> search header <-> results,
            // instead of Back sitting directly above the results with no
            // way to land on the header row via Up/Down at all.
            if (CloseButton.IsKeyboardFocused)
            {
                if (direction > 0)
                {
                    FocusSearchBarRow();
                }

                return;
            }

            if (IsSearchBarRowFocused())
            {
                if (direction < 0)
                {
                    CloseButton.Focus();
                }
                else
                {
                    FocusFirstAvailableSection();
                }

                return;
            }

            MediaSection[] sections = GetVisibleSections();

            if (sections.Length == 0)
            {
                CloseButton.Focus();
                return;
            }

            MediaSection current = GetFocusedSection();
            int index = Array.IndexOf(sections, current);

            if (index < 0)
            {
                sections[0].FocusFirstCard();
                return;
            }

            int target = index + direction;

            if (target < 0)
            {
                // Search results Up-navigation fix: moving up out of the
                // topmost results section now returns to the search
                // header row (SearchBox), not all the way to the page-
                // level Back button - Back is one more Up away from
                // there, matching the chain established above.
                FocusSearchBarRow();
                return;
            }

            target = Math.Min(target, sections.Length - 1);
            sections[target].FocusFirstCard();
        }

        private bool IsSearchBarRowFocused()
        {
            return SearchBox.IsKeyboardFocusWithin ||
                   OpenKeyboardButton.IsKeyboardFocused ||
                   SearchButton.IsKeyboardFocused;
        }

        private void FocusSearchBarRow()
        {
            FocusSearchBox();
        }

        // Search-bar horizontal navigation task: SearchBox -> Right ->
        // on-screen-keyboard button -> Right -> Search button, and the
        // exact reverse with Left. Each stop clamps at its own end of the
        // row (SearchBox is the leftmost, SearchButton the rightmost) -
        // same clamp convention MediaSection.MoveHorizontal already uses
        // at the right-hand end of a card row (see that method's own
        // remarks). Falls through to the existing card-row behaviour
        // unchanged when none of the three search-bar controls has
        // focus.
        private void MoveHorizontal(int direction)
        {
            if (SearchBox.IsKeyboardFocusWithin)
            {
                if (direction > 0)
                {
                    OpenKeyboardButton.Focus();
                }

                // direction < 0: SearchBox is the leftmost stop in this
                // row - Left here is always ordinary caret movement,
                // handled by the TextBox itself before this method is
                // ever reached (see SearchView_PreviewKeyDown's guard),
                // so there is nothing further to do on that path.
                return;
            }

            if (OpenKeyboardButton.IsKeyboardFocused)
            {
                if (direction > 0)
                {
                    SearchButton.Focus();
                }
                else
                {
                    FocusSearchBox();
                }

                return;
            }

            if (SearchButton.IsKeyboardFocused)
            {
                if (direction < 0)
                {
                    OpenKeyboardButton.Focus();
                }

                // direction > 0: already the rightmost stop in this row -
                // no-op.
                return;
            }

            GetFocusedSection()?.MoveHorizontal(direction);
        }

        private void FocusFirstAvailableSection()
        {
            MediaSection[] sections = GetVisibleSections();

            if (sections.Length > 0)
            {
                sections[0].FocusFirstCard();
            }
        }
    }
}
