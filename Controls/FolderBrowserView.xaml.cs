using Playnite.SDK.Events;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
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
    // FOLDER BROWSER VIEW (Local Files - in-app folder picker)
    // =============================================================
    //
    // A controller-friendly replacement for the native Windows folder
    // picker: drives -> folders, with Up, "Select this folder" and Cancel.
    // Every control is an ordinary focusable Button, so the existing
    // keyboard / D-pad / A / B conventions apply (A clicks the focused
    // button, B cancels). Directional focus is contained inside this panel.
    //
    // It only browses. Choosing a folder raises FolderChosen(path); the
    // owner (LocalMediaView) passes that to the existing
    // LocalLibraryManager.AddFolder, which keeps doing the normalisation,
    // de-duplication, overlap checks, persistence and scanning. Cancelling
    // raises Cancelled and never adds anything.
    //
    // Listing runs on a background thread (a disconnected share can block),
    // and a result only applies if it is still the latest request.
    public partial class FolderBrowserView : UserControl
    {
        public event EventHandler<string> FolderChosen;
        public event EventHandler Cancelled;

        // Row shown in the list (a drive or a folder).
        internal sealed class BrowserRow
        {
            public string Title { get; set; }
            public string Detail { get; set; }
            public string Path { get; set; }

            public Visibility DetailVisibility
            {
                get
                {
                    return string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        private const string DrivesTitle = "This PC (choose a drive)";

        // null = the drive list; otherwise the folder being shown.
        private string _currentPath;
        private bool _currentIsListable;
        private int _requestVersion;

        public FolderBrowserView()
        {
            InitializeComponent();

            KeyboardNavigation.SetDirectionalNavigation(RootGrid, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetTabNavigation(RootGrid, KeyboardNavigationMode.Contained);

            // Keep the focused entry visible while moving through a long list.
            AddHandler(
                Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(OnAnyGotKeyboardFocus),
                true);

            Visibility = Visibility.Collapsed;
        }

        public bool IsOpen
        {
            get { return Visibility == Visibility.Visible; }
        }

        // =========================================================
        // OPEN / CLOSE
        // =========================================================

        public void Open()
        {
            Visibility = Visibility.Visible;
            ShowDrives(null);
        }

        public void Close()
        {
            // Invalidates any listing still in flight.
            _requestVersion++;
            Visibility = Visibility.Collapsed;
        }

        // =========================================================
        // NAVIGATION
        // =========================================================

        private async void ShowDrives(string focusPath)
        {
            int version = ++_requestVersion;

            SetBusy("Reading drives...");

            List<FolderEntry> drives = await Task.Run(() => FolderBrowserService.GetDrives());

            if (version != _requestVersion)
            {
                return;
            }

            _currentPath = null;
            _currentIsListable = false;

            PathText.Text = DrivesTitle;
            CaptionText.Text = "AVAILABLE DRIVES";
            ShowStatus(null);

            BindRows(
                drives.Select(d => new BrowserRow { Title = d.Name, Detail = d.Detail, Path = d.Path }).ToList(),
                drives.Count == 0 ? "No drives were found." : null,
                focusPath);

            UpdateButtons();
        }

        private async void NavigateTo(string path, string focusPath)
        {
            int version = ++_requestVersion;

            SetBusy("Opening folder...");

            FolderListing listing = await Task.Run(() => FolderBrowserService.List(path));

            if (version != _requestVersion)
            {
                return;
            }

            if (listing.Status != FolderListingStatus.Ok)
            {
                // Stay where we were: a folder that cannot be opened is not
                // navigated into, and nothing is selected.
                RestoreAfterFailure(DescribeFailure(listing.Status));
                return;
            }

            _currentPath = path;
            _currentIsListable = true;

            PathText.Text = path;
            CaptionText.Text = "CURRENT FOLDER - \"Select this folder\" adds this one";
            ShowStatus(listing.Truncated
                ? "This folder has more than " + FolderBrowserService.MaxEntries + " sub-folders; only the first are shown."
                : null);

            BindRows(
                listing.Entries.Select(e => new BrowserRow { Title = e.Name, Detail = e.Detail, Path = e.Path }).ToList(),
                "No sub-folders here. You can still select this folder.",
                focusPath);

            UpdateButtons();
        }

        private static string DescribeFailure(FolderListingStatus status)
        {
            switch (status)
            {
                case FolderListingStatus.AccessDenied:
                    return "That folder can't be opened (access denied).";

                case FolderListingStatus.NotFound:
                    return "That folder no longer exists, or the drive isn't connected.";

                default:
                    return "That location isn't available right now.";
            }
        }

        // A failed navigation leaves the previous view in place (its rows are
        // still bound); only the message changes.
        private void RestoreAfterFailure(string message)
        {
            EmptyText.Visibility = EntriesControl.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (EntriesControl.Items.Count == 0)
            {
                EmptyText.Text = "Nothing to show.";
            }

            ShowStatus(message);
            UpdateButtons();
            FocusFirst();
        }

        private void SetBusy(string message)
        {
            ShowStatus(message);
            UpdateButtons(true);
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BindRows(List<BrowserRow> rows, string emptyMessage, string focusPath)
        {
            EntriesControl.ItemsSource = rows;
            EntriesScrollViewer.ScrollToVerticalOffset(0);

            if (rows.Count == 0)
            {
                EmptyText.Text = emptyMessage ?? string.Empty;
                EmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyText.Visibility = Visibility.Collapsed;
            }

            // Initial focus: the folder we just came from (after Up), else the
            // first entry, else the primary action.
            Dispatcher.BeginInvoke(
                new Action(() => FocusRow(focusPath, 0)),
                DispatcherPriority.Loaded);
        }

        private void UpdateButtons(bool busy = false)
        {
            // Up is available inside a folder; from a drive root it returns to
            // the drive list. Nothing to go up from at the drive list.
            UpButton.IsEnabled = !busy && _currentPath != null;

            // Only a real, openable folder can be selected (not the drive
            // list, and not while a listing is loading).
            SelectButton.IsEnabled = !busy && _currentPath != null && _currentIsListable;
        }

        // =========================================================
        // BUTTONS
        // =========================================================

        private void EntryButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var row = button != null ? button.Tag as BrowserRow : null;

            if (row == null || string.IsNullOrEmpty(row.Path))
            {
                return;
            }

            NavigateTo(row.Path, null);
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            GoUp();
        }

        private void GoUp()
        {
            if (_currentPath == null)
            {
                return;
            }

            string came = _currentPath;
            string parent = FolderBrowserService.GetParent(_currentPath);

            if (parent == null)
            {
                ShowDrives(came);
            }
            else
            {
                NavigateTo(parent, came);
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            string path = _currentPath;

            if (string.IsNullOrEmpty(path) || !_currentIsListable)
            {
                return;
            }

            EventHandler<string> handler = FolderChosen;

            if (handler != null)
            {
                handler(this, path);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseCancelled();
        }

        private void RaiseCancelled()
        {
            EventHandler handler = Cancelled;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // =========================================================
        // INPUT
        // =========================================================

        // A clicks the focused button, B cancels (never adds a folder), the
        // D-pad moves focus inside the panel. Escape reaches here as B.
        public bool HandleControllerInput(ControllerInput button)
        {
            if (button == ControllerInput.B)
            {
                RaiseCancelled();
                return true;
            }

            if (button == ControllerInput.A)
            {
                var focusedButton = Keyboard.FocusedElement as ButtonBase;

                if (focusedButton != null && focusedButton.IsEnabled)
                {
                    focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }

                return true;
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

            var focused = Keyboard.FocusedElement as UIElement;

            if (focused == null || !IsDescendantOfThis(focused))
            {
                FocusFirst();
                return true;
            }

            focused.MoveFocus(new TraversalRequest(direction));

            return true;
        }

        private bool IsDescendantOfThis(DependencyObject element)
        {
            DependencyObject current = element;

            while (current != null)
            {
                if (ReferenceEquals(current, this))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        // =========================================================
        // FOCUS
        // =========================================================

        private void FocusFirst()
        {
            FocusRow(null, 0);
        }

        private void FocusRow(string preferredPath, int attempt)
        {
            if (!IsOpen)
            {
                return;
            }

            Button target = null;
            int count = EntriesControl.Items.Count;

            for (int i = 0; i < count; i++)
            {
                var presenter = EntriesControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;

                if (presenter == null)
                {
                    // Containers not generated yet: retry a bounded number of times.
                    if (attempt < 20)
                    {
                        Dispatcher.BeginInvoke(
                            new Action(() => FocusRow(preferredPath, attempt + 1)),
                            DispatcherPriority.Loaded);
                        return;
                    }

                    break;
                }

                Button candidate = FindButton(presenter);

                if (candidate == null)
                {
                    continue;
                }

                var row = candidate.Tag as BrowserRow;

                if (target == null)
                {
                    target = candidate;
                }

                if (preferredPath != null && row != null &&
                    string.Equals(row.Path, preferredPath, StringComparison.OrdinalIgnoreCase))
                {
                    target = candidate;
                    break;
                }

                if (preferredPath == null)
                {
                    break;
                }
            }

            if (target == null)
            {
                target = SelectButton.IsEnabled ? SelectButton : (Button)CancelButton;
            }

            target.Focus();
            Keyboard.Focus(target);
        }

        private static Button FindButton(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                var button = child as Button;

                if (button != null)
                {
                    return button;
                }

                button = FindButton(child);

                if (button != null)
                {
                    return button;
                }
            }

            return null;
        }

        private void OnAnyGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var element = e.NewFocus as FrameworkElement;

            if (element != null && element.Tag is BrowserRow)
            {
                element.BringIntoView();
            }
        }
    }
}
