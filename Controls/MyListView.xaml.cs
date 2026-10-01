using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // MY LIST VIEW (Slice 17B - My List UI + Hero Integration)
    // =============================================================
    //
    // Profile-owned "what you've saved" page. Mirrors the read-only
    // display conventions HistoryView already established (Initialize
    // handed already-built data, never touches a service directly -
    // StremioVideoCenterView.ShowMyListAsync owns MyListService and the
    // active profile, builds the MyListEntry list, and is the only place
    // that calls MyListService.Remove) plus the filtered-grid navigation
    // conventions CatalogueBrowseView/AnimeBrowseView already established
    // (flat-index WrapPanel grid math, raw MediaCard outside any
    // MediaSection, hand-rolled focus chain instead of WPF's built-in
    // directional navigation).
    //
    // Filtering (Media Type + Genre) is entirely client-side against the
    // MyListEntry list this view was handed - no network request, per
    // the spec's explicit requirement. CanonicalKey (MediaItem.StremioId
    // on the display items built from it) is the only identity ever
    // used - never title/poster/display text - so removal is always
    // unambiguous and always series-level for TV/Anime, matching
    // MyListEntry's own identity contract.
    public partial class MyListView : UserControl
    {
        // MediaCard's own fixed Border Width (190) plus its Margin (8 on
        // every side = 16 total) - identical constant to CatalogueBrowse
        // View.FilteredCardWidth/AnimeBrowseView's own copy, used the
        // same way: estimating how many columns the WrapPanel is
        // currently laying out so Up/Down can move by a row.
        private const double ResultsCardWidth = 206;

        public event EventHandler Closed;
        public event EventHandler<MediaItemSelectedEventArgs> MediaSelected;
        public event EventHandler<MyListEntryRemovedEventArgs> EntryRemoveRequested;

        private List<MyListEntry> _allEntries = new List<MyListEntry>();

        private readonly ObservableCollection<MediaItem> _filteredDisplayItems =
            new ObservableCollection<MediaItem>();

        private readonly ObservableCollection<MyListGenreOption> _genreOptions =
            new ObservableCollection<MyListGenreOption>();

        private ToggleButton[] _mediaTypeButtons;

        // null = "ALL" (no content-type filter). Combined with
        // _selectedGenre via a plain AND - see ApplyFilters.
        private ContentType? _selectedContentType;

        // null = "All Genres" (no genre filter).
        private string _selectedGenre;

        private const string AllGenresLabel = "All Genres";

        public MyListView()
        {
            InitializeComponent();

            // Catches the bubbling MediaCard.SelectedEvent raised by the
            // raw MediaCards inside ResultsItemsControl - same convention
            // CatalogueBrowseView/AnimeBrowseView already use for their
            // own non-MediaSection card grids.
            AddHandler(MediaCard.SelectedEvent, new RoutedEventHandler(ResultCard_Selected));

            _mediaTypeButtons = new[] { AllTypeButton, MoviesTypeButton, TvTypeButton, AnimeTypeButton };

            GenreItemsControl.ItemsSource = _genreOptions;
            ResultsItemsControl.ItemsSource = _filteredDisplayItems;

            Loaded += MyListView_Loaded;
        }

        // =========================================================
        // INITIALIZE (fresh open)
        // =========================================================
        //
        // entries are the active profile's current My List entries,
        // already fetched by the caller (see StremioVideoCenterView.
        // ShowMyListAsync) - this view never calls MyListService itself,
        // mirroring HistoryView.Initialize exactly. Resets both filters
        // to their defaults (ALL / All Genres), matching a fresh page
        // open.
        public void Initialize(List<MyListEntry> entries)
        {
            _allEntries = entries ?? new List<MyListEntry>();

            _selectedContentType = null;
            _selectedGenre = null;

            SelectMediaTypeButtonWithoutApplying(AllTypeButton);

            RebuildGenreOptions();
            ApplyFilters();
        }

        // =========================================================
        // UPDATE ENTRIES (in-place refresh after a removal)
        // =========================================================
        //
        // Removal requirement: "Update the displayed list immediately...
        // Do not require a restart... If the currently displayed item is
        // removed, focus must move predictably to a neighbouring item."
        // Preserves the current Media Type/Genre selection (unlike
        // Initialize) - a removal should not silently reset what the
        // user was filtering by. Falls back to "All Genres" only if the
        // previously-selected genre no longer exists in any remaining
        // entry.
        public void UpdateEntries(List<MyListEntry> entries)
        {
            int focusedIndex = GetFocusedResultIndex();

            _allEntries = entries ?? new List<MyListEntry>();

            RebuildGenreOptions();

            if (_selectedGenre != null &&
                !_genreOptions.Any(option =>
                    !option.IsAllOption &&
                    string.Equals(option.Name, _selectedGenre, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedGenre = null;
            }

            SelectGenreButtonWithoutApplying(_selectedGenre);

            ApplyFilters();

            FocusResultNeighbor(focusedIndex);
        }

        private void MyListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_allEntries.Count == 0)
            {
                CloseButton.Focus();
                return;
            }

            AllTypeButton.Focus();
        }

        // =========================================================
        // GENRE OPTIONS (derived from the profile's full My List, not
        // re-derived per Media Type selection - see class remarks: a
        // genre chip's availability does not change as the Media Type
        // filter changes, only which results are visible does)
        // =========================================================

        private void RebuildGenreOptions()
        {
            _genreOptions.Clear();

            _genreOptions.Add(new MyListGenreOption { Name = AllGenresLabel, IsAllOption = true });

            var distinctGenres = _allEntries
                .Where(entry => entry != null && entry.Genres != null)
                .SelectMany(entry => entry.Genres)
                .Where(genre => !string.IsNullOrWhiteSpace(genre))
                .GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase);

            foreach (string genre in distinctGenres)
            {
                _genreOptions.Add(new MyListGenreOption { Name = genre, IsAllOption = false });
            }
        }

        // =========================================================
        // FILTERING (client-side only - no network request)
        // =========================================================

        private void ApplyFilters()
        {
            IEnumerable<MyListEntry> filtered = _allEntries;

            if (_selectedContentType.HasValue)
            {
                filtered = filtered.Where(entry => entry.ContentType == _selectedContentType.Value);
            }

            if (!string.IsNullOrEmpty(_selectedGenre))
            {
                filtered = filtered.Where(entry =>
                    entry.Genres != null &&
                    entry.Genres.Any(genre =>
                        string.Equals(genre, _selectedGenre, StringComparison.OrdinalIgnoreCase)));
            }

            List<MyListEntry> ordered = filtered
                .OrderByDescending(entry => entry.AddedUtc)
                .ToList();

            _filteredDisplayItems.Clear();

            foreach (MyListEntry entry in ordered)
            {
                _filteredDisplayItems.Add(BuildDisplayItem(entry));
            }

            UpdateEmptyState();
        }

        private static MediaItem BuildDisplayItem(MyListEntry entry)
        {
            return new MediaItem
            {
                // Show/movie-level identity only - see MyListEntry's own
                // class remarks. Never an episode-qualified id, so
                // selecting a TV/Anime card here always lands on
                // EpisodeBrowserView via the existing HandleMediaSelected
                // Async routing, never a specific episode directly.
                StremioId = entry.CanonicalKey,
                Title = entry.Title,
                PosterPath = entry.PosterPath,
                Year = entry.Year,
                Type = entry.Type,
                Genres = entry.Genres
            };
        }

        private void UpdateEmptyState()
        {
            bool hasAnyEntries = _allEntries.Count > 0;
            bool hasFilteredResults = _filteredDisplayItems.Count > 0;

            // No saved entries at all yet - the filter row itself is
            // pointless (nothing to filter), so it's hidden entirely
            // rather than shown empty.
            FiltersPanel.Visibility = hasAnyEntries ? Visibility.Visible : Visibility.Collapsed;

            ResultsItemsControl.Visibility = hasFilteredResults ? Visibility.Visible : Visibility.Collapsed;

            EmptyListText.Visibility = !hasAnyEntries ? Visibility.Visible : Visibility.Collapsed;

            NoMatchText.Visibility =
                hasAnyEntries && !hasFilteredResults ? Visibility.Visible : Visibility.Collapsed;
        }

        // =========================================================
        // MEDIA TYPE CHIPS (single-select)
        // =========================================================
        //
        // Click handler covers mouse (WPF's native ToggleButton click
        // already flipped IsChecked by the time Click fires - see
        // AnimeBrowseView's ActivateKitsuCategoryChip remarks for why
        // that ordering matters). Keyboard/controller activation goes
        // through ActivateMediaTypeChip below, which sets IsChecked
        // directly rather than raising Click (raising the Click routed
        // event alone never invokes ToggleButton's own IsChecked-flipping
        // OnClick override - same root cause already fixed for Anime's
        // category chips) and then calls the exact same SelectMediaType
        // shared method, so both input paths end up identical.

        private void MediaTypeChip_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton clicked = sender as ToggleButton;

            if (clicked != null)
            {
                SelectMediaType(clicked);
            }
        }

        private bool ActivateMediaTypeChip()
        {
            ToggleButton focused = _mediaTypeButtons.FirstOrDefault(
                button => button != null && button.IsKeyboardFocused);

            if (focused == null)
            {
                return false;
            }

            SelectMediaType(focused);

            return true;
        }

        // Single-select ("radio") enforcement: always ends with exactly
        // one of the four buttons checked - clicking/activating the
        // already-selected chip is a harmless no-op rather than being
        // allowed to toggle off into an invalid "nothing selected" state
        // (unlike Anime's independent multi-select categories).
        private void SelectMediaType(ToggleButton chip)
        {
            chip.IsChecked = true;

            foreach (ToggleButton button in _mediaTypeButtons)
            {
                if (button != null && !ReferenceEquals(button, chip))
                {
                    button.IsChecked = false;
                }
            }

            _selectedContentType = ResolveContentTypeFor(chip);

            ApplyFilters();
        }

        private void SelectMediaTypeButtonWithoutApplying(ToggleButton chip)
        {
            foreach (ToggleButton button in _mediaTypeButtons)
            {
                if (button != null)
                {
                    button.IsChecked = ReferenceEquals(button, chip);
                }
            }
        }

        private ContentType? ResolveContentTypeFor(ToggleButton chip)
        {
            if (ReferenceEquals(chip, MoviesTypeButton))
            {
                return ContentType.Movie;
            }

            if (ReferenceEquals(chip, TvTypeButton))
            {
                return ContentType.TvShow;
            }

            if (ReferenceEquals(chip, AnimeTypeButton))
            {
                return ContentType.Anime;
            }

            return null;
        }

        // =========================================================
        // GENRE CHIPS (single-select, dynamic)
        // =========================================================

        private void GenreChip_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton clicked = sender as ToggleButton;

            if (clicked == null)
            {
                return;
            }

            MyListGenreOption option = clicked.DataContext as MyListGenreOption;

            SelectGenre(clicked, option);
        }

        private bool ActivateGenreChip()
        {
            int index = GetFocusedGenreIndex();

            if (index < 0)
            {
                return false;
            }

            ToggleButton chip = GetGenreContainer(index);

            if (chip == null)
            {
                return false;
            }

            SelectGenre(chip, _genreOptions[index]);

            return true;
        }

        private void SelectGenre(ToggleButton chip, MyListGenreOption option)
        {
            chip.IsChecked = true;

            for (int i = 0; i < GenreItemsControl.Items.Count; i++)
            {
                ToggleButton container = GetGenreContainer(i);

                if (container != null && !ReferenceEquals(container, chip))
                {
                    container.IsChecked = false;
                }
            }

            _selectedGenre = option != null && !option.IsAllOption ? option.Name : null;

            ApplyFilters();
        }

        // Re-applies a previously-known genre selection (or "All Genres"
        // when null/no-longer-valid) onto the chip containers directly,
        // without re-triggering ApplyFilters - used by UpdateEntries,
        // which calls ApplyFilters itself once afterward.
        private void SelectGenreButtonWithoutApplying(string genreName)
        {
            for (int i = 0; i < _genreOptions.Count; i++)
            {
                MyListGenreOption option = _genreOptions[i];

                bool shouldBeChecked = genreName == null
                    ? option.IsAllOption
                    : !option.IsAllOption && string.Equals(option.Name, genreName, StringComparison.OrdinalIgnoreCase);

                ToggleButton container = GetGenreContainer(i);

                if (container != null)
                {
                    container.IsChecked = shouldBeChecked;
                }
            }
        }

        private ToggleButton GetGenreContainer(int index)
        {
            if (index < 0 || index >= GenreItemsControl.Items.Count)
            {
                return null;
            }

            ContentPresenter presenter =
                GenreItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            return FindVisualChild<ToggleButton>(presenter);
        }

        private int GetFocusedGenreIndex()
        {
            for (int i = 0; i < GenreItemsControl.Items.Count; i++)
            {
                ToggleButton container = GetGenreContainer(i);

                if (container != null && container.IsKeyboardFocused)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusGenreChip(int index)
        {
            ToggleButton container = GetGenreContainer(index);
            container?.Focus();
        }

        // =========================================================
        // RESULT SELECTION (existing media-selection pipeline)
        // =========================================================

        private void ResultCard_Selected(object sender, RoutedEventArgs e)
        {
            MediaCard card = e.OriginalSource as MediaCard;

            if (card == null)
            {
                return;
            }

            MediaItem mediaItem = card.DataContext as MediaItem;

            if (mediaItem == null)
            {
                return;
            }

            MediaSelected?.Invoke(this, new MediaItemSelectedEventArgs(mediaItem));

            e.Handled = true;
        }

        // =========================================================
        // REMOVAL
        // =========================================================
        //
        // Never touches MyListService directly (see class remarks) -
        // only raises EntryRemoveRequested with the CanonicalKey, which
        // StremioVideoCenterView.ShowMyListAsync (the only place that
        // owns MyListService + the active profile) acts on, then calls
        // UpdateEntries back with the fresh list.

        private void RemoveChipButton_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement button = sender as FrameworkElement;
            MediaItem mediaItem = button?.DataContext as MediaItem;

            if (mediaItem != null)
            {
                RaiseEntryRemoveRequested(mediaItem.StremioId);
            }

            e.Handled = true;
        }

        private bool TryRemoveFocusedResult()
        {
            MediaCard card = FindFocusedMediaCard(ResultsItemsControl);

            if (card == null)
            {
                return false;
            }

            MediaItem mediaItem = card.DataContext as MediaItem;

            if (mediaItem == null)
            {
                return false;
            }

            RaiseEntryRemoveRequested(mediaItem.StremioId);

            return true;
        }

        private void RaiseEntryRemoveRequested(string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return;
            }

            EntryRemoveRequested?.Invoke(this, new MyListEntryRemovedEventArgs(canonicalKey));
        }

        // =========================================================
        // CLOSE
        // =========================================================

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseClosed();
        }

        private void RaiseClosed()
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void MyListView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    RaiseClosed();
                    e.Handled = true;
                    break;

                // Removal task: Delete removes the focused card's entry -
                // keyboard equivalent of the per-card "X" mouse button
                // and Controller X (see HandleControllerInput below).
                case Key.Delete:
                    if (TryRemoveFocusedResult())
                    {
                        e.Handled = true;
                    }
                    break;

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
                case Key.Space:
                    if (ActivateFocusedElement())
                    {
                        e.Handled = true;
                    }
                    break;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            if (button == ControllerInput.B)
            {
                RaiseClosed();
                return true;
            }

            if (button == ControllerInput.A)
            {
                return ActivateFocusedElement();
            }

            // Removal task: Controller X removes the focused card's
            // entry - no existing Zino convention claims this button
            // anywhere else in the app (only A/B/DPad are used
            // elsewhere), so it is free to adopt here as this page's own
            // dedicated remove action, mirroring the keyboard Delete key
            // above.
            if (button == ControllerInput.X)
            {
                return TryRemoveFocusedResult();
            }

            if (button == ControllerInput.DPadUp)
            {
                MoveVertical(-1);
                return true;
            }

            if (button == ControllerInput.DPadDown)
            {
                MoveVertical(1);
                return true;
            }

            if (button == ControllerInput.DPadLeft)
            {
                MoveHorizontal(-1);
                return true;
            }

            if (button == ControllerInput.DPadRight)
            {
                MoveHorizontal(1);
                return true;
            }

            return false;
        }

        // =========================================================
        // ACTIVATION (shared by keyboard Enter/Space and controller A)
        // =========================================================

        private bool ActivateFocusedElement()
        {
            if (ActivateMediaTypeChip())
            {
                return true;
            }

            if (ActivateGenreChip())
            {
                return true;
            }

            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return ActivateFocusedResultCard();
        }

        private bool ActivateFocusedResultCard()
        {
            MediaCard card = FindFocusedMediaCard(ResultsItemsControl);

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
        // FOCUS MOVEMENT (shared by keyboard + controller)
        // =========================================================
        //
        // Four zones, top to bottom: Header (CloseButton) <-> Media Type
        // row <-> Genre row <-> Results grid. Left/Right cycles within
        // whichever zone has focus (a flat index for Media Type/Genre,
        // since those chips are not a fixed-width grid); Up/Down always
        // moves a whole zone at a time - including out of the Genre row,
        // even if its chips happen to currently wrap onto more than one
        // visual line (a simpler, intentionally less granular version of
        // AnimeBrowseView's own wrapped-category-row vertical math,
        // acceptable here since My List's genre set is typically small -
        // every chip, wrapped or not, always remains reachable via
        // Left/Right).

        private void MoveVertical(int direction)
        {
            if (CloseButton.IsKeyboardFocused)
            {
                if (direction > 0)
                {
                    FocusFirstAvailable();
                }

                return;
            }

            if (IsMediaTypeRowFocused())
            {
                if (direction < 0)
                {
                    CloseButton.Focus();
                }
                else
                {
                    FocusGenreChip(0);
                }

                return;
            }

            if (IsGenreRowFocused())
            {
                if (direction < 0)
                {
                    FocusCurrentMediaTypeButton();
                }
                else if (_filteredDisplayItems.Count > 0)
                {
                    FocusResultCard(0);
                }

                return;
            }

            if (IsResultsGridFocused())
            {
                if (direction < 0 && GetFocusedResultIndex() < GetResultsColumnsPerRow())
                {
                    if (FiltersPanel.Visibility == Visibility.Visible)
                    {
                        int genreIndex = GetSelectedGenreIndex();
                        FocusGenreChip(genreIndex >= 0 ? genreIndex : 0);
                    }
                    else
                    {
                        CloseButton.Focus();
                    }

                    return;
                }

                MoveResultsGrid(0, direction);
            }
        }

        private void MoveHorizontal(int direction)
        {
            if (IsMediaTypeRowFocused())
            {
                int currentIndex = Array.FindIndex(
                    _mediaTypeButtons,
                    button => button != null && button.IsKeyboardFocused);

                if (currentIndex < 0)
                {
                    return;
                }

                int target = Math.Max(0, Math.Min(currentIndex + direction, _mediaTypeButtons.Length - 1));
                _mediaTypeButtons[target]?.Focus();
                return;
            }

            if (IsGenreRowFocused())
            {
                int currentIndex = GetFocusedGenreIndex();

                if (currentIndex < 0)
                {
                    return;
                }

                int target = Math.Max(0, Math.Min(currentIndex + direction, GenreItemsControl.Items.Count - 1));
                FocusGenreChip(target);
                return;
            }

            if (IsResultsGridFocused())
            {
                MoveResultsGrid(direction, 0);
            }
        }

        private bool IsMediaTypeRowFocused()
        {
            return Array.Exists(_mediaTypeButtons, button => button != null && button.IsKeyboardFocused);
        }

        private bool IsGenreRowFocused()
        {
            return GetFocusedGenreIndex() >= 0;
        }

        private bool IsResultsGridFocused()
        {
            return GetFocusedResultIndex() >= 0;
        }

        private void FocusCurrentMediaTypeButton()
        {
            ToggleButton selected = _mediaTypeButtons.FirstOrDefault(button => button != null && button.IsChecked == true)
                ?? AllTypeButton;

            selected.Focus();
        }

        private int GetSelectedGenreIndex()
        {
            for (int i = 0; i < _genreOptions.Count; i++)
            {
                ToggleButton container = GetGenreContainer(i);

                if (container != null && container.IsChecked == true)
                {
                    return i;
                }
            }

            return -1;
        }

        private void FocusFirstAvailable()
        {
            if (FiltersPanel.Visibility == Visibility.Visible)
            {
                AllTypeButton.Focus();
            }
            else if (_filteredDisplayItems.Count > 0)
            {
                FocusResultCard(0);
            }
            else
            {
                CloseButton.Focus();
            }
        }

        // =========================================================
        // RESULTS GRID FOCUS MOVEMENT
        // =========================================================
        //
        // Identical shape to CatalogueBrowseView's own filtered-grid
        // movement (MoveFilteredGrid/GetFocusedFilteredCardIndex/
        // FocusFilteredCard) - a plain WrapPanel of fixed-width
        // MediaCards, not a MediaSection, so it needs the same index
        // math + ItemContainerGenerator-based container lookup.

        private int GetResultsColumnsPerRow()
        {
            double width = ResultsItemsControl.ActualWidth;

            if (width <= 0)
            {
                return 1;
            }

            int columns = (int)(width / ResultsCardWidth);

            return Math.Max(1, columns);
        }

        private void MoveResultsGrid(int deltaColumns, int deltaRows)
        {
            int count = _filteredDisplayItems.Count;

            if (count == 0)
            {
                return;
            }

            int current = GetFocusedResultIndex();

            if (current < 0)
            {
                FocusResultCard(0);
                return;
            }

            int columns = GetResultsColumnsPerRow();
            int target = current + deltaColumns + (deltaRows * columns);

            target = Math.Max(0, Math.Min(target, count - 1));

            FocusResultCard(target);
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

        private void FocusResultCard(int index)
        {
            if (index < 0 || index >= ResultsItemsControl.Items.Count)
            {
                return;
            }

            ContentPresenter presenter =
                ResultsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;

            if (presenter == null)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FocusResultCard(index)),
                    DispatcherPriority.Loaded);

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

        // Removal task: after UpdateEntries rebuilds the filtered list,
        // focus lands on whichever card now occupies the same index
        // (the item that slid up to take the removed one's place),
        // clamped to the new (possibly smaller) count - falling back to
        // the filter row, then Back, when nothing is left to focus. This
        // satisfies "focus must move predictably to a neighbouring item"
        // without guessing which specific neighbour the user would
        // expect - the same position is the least surprising choice.
        private void FocusResultNeighbor(int previousIndex)
        {
            if (_filteredDisplayItems.Count == 0)
            {
                FocusFirstAvailable();
                return;
            }

            int target = Math.Max(0, Math.Min(previousIndex, _filteredDisplayItems.Count - 1));

            Dispatcher.BeginInvoke(
                new Action(() => FocusResultCard(target)),
                DispatcherPriority.Loaded);
        }

        // =========================================================
        // VISUAL TREE SEARCH
        // =========================================================

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

        // =========================================================
        // GENRE OPTION (local display model - plain, no
        // INotifyPropertyChanged, same convention as AnimeBrowseView's
        // own KitsuCategoryOption: selection is driven entirely by
        // directly setting each chip's own IsChecked in code, never via
        // a bound IsSelected property, so no change-notification path is
        // needed back out of this class)
        // =========================================================

        private class MyListGenreOption
        {
            public string Name { get; set; }

            public bool IsAllOption { get; set; }
        }
    }

    // =============================================================
    // MY LIST ENTRY REMOVED EVENT ARGS
    // =============================================================

    public class MyListEntryRemovedEventArgs : EventArgs
    {
        public string CanonicalKey { get; }

        public MyListEntryRemovedEventArgs(string canonicalKey)
        {
            CanonicalKey = canonicalKey;
        }
    }
}
