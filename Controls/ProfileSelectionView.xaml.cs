using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    // =============================================================
    // PROFILE SELECTION VIEW (Slice 16A - Profile Foundation)
    // =============================================================
    //
    // Shared Netflix-style "Who's watching?" overlay - see the XAML
    // file's own header remarks for why this one implementation serves
    // both application startup and SWITCH PROFILE.
    //
    // Same overlay conventions as every other overlay in this codebase:
    // created fresh per visit (StremioVideoCenterView.
    // ShowProfileSelectionAsync), a single ProfileSelected event bridged
    // into an awaitable Task by the caller, PreviewKeyDown for keyboard,
    // HandleControllerInput for the shared "most specific overlay wins"
    // input chain.
    public partial class ProfileSelectionView : UserControl
    {
        private enum NameEntryMode
        {
            None,
            Add,
            Rename
        }

        public event EventHandler<Guid> ProfileSelected;

        private ProfileService _profileService;

        // The list of every focusable tile-row button (select, plus Add
        // Profile's own button) in tile order, so directional focus
        // movement has something deterministic to walk - built fresh every
        // BuildProfileTiles() call. Rename/Delete buttons are reached by
        // WPF's own PredictFocus (see MoveFocus) rather than tracked here,
        // the same "let PredictFocus handle the uneven grid" reasoning
        // OnScreenKeyboardView already uses.
        private readonly List<Button> _selectButtons = new List<Button>();

        // 16A fix #1 (focus indication): maps every tile-row button to the
        // avatar/plus Border it should draw a focus ring around - these
        // Borders are plain Button.Content, not a ControlTemplate part, so
        // they cannot be styled with a XAML Trigger the way
        // ProfileRenameButtonStyle/ProfileDeleteButtonStyle are; this is
        // the same PreviewGotKeyboardFocus/PreviewLostKeyboardFocus
        // technique MediaCard already uses for its own CardBorder.
        private readonly Dictionary<Button, Border> _focusRingForButton =
            new Dictionary<Button, Border>();

        // The ring's own un-focused/un-hovered appearance, captured at
        // RegisterFocusRing() time - a select tile's avatarBorder starts
        // Transparent, but the Add Profile tile's plusBorder starts with
        // its own permanent grey outline (#55555F), which the focus ring
        // must restore rather than clobber to Transparent when focus/hover
        // ends.
        private readonly Dictionary<Border, Brush> _idleBrushForRing =
            new Dictionary<Border, Brush>();

        // 16A active-keyboard-focus fix: every Button under ProfileTiles
        // Panel (select, Edit name, Delete profile - all three, not just
        // the _selectButtons list above), captured with its actual
        // Focusable value right before NameEntryPanel opens and restored
        // exactly on close. Needed because OnScreenKeyboardView.MoveFocus's
        // PredictFocus() call searches the whole connected visual tree by
        // on-screen geometry only - it has no idea ProfileTilesPanel is
        // visually covered by NameEntryPanel's higher Panel.ZIndex (the
        // same "still visible, just covered" shape as the 16A Issue 1 fix
        // above, just hit through directional arrow navigation instead of
        // WPF's automatic focus reassignment) - so a tile Button sitting
        // underneath the modal was being handed back as the "nearest"
        // Right/Down/etc. candidate instead of the next on-screen-keyboard
        // key, and OnScreenKeyboardView's own IsDescendantOf guard then
        // correctly refused to focus it, silently swallowing the key press
        // instead of moving to the intended key. Suspending focusability
        // (not IsEnabled/IsHitTestVisible - mouse interaction with the
        // tiles was never the problem, and disabling would additionally
        // grey them out) removes every tile Button from PredictFocus's
        // candidate set for as long as the modal is open, exactly as if
        // ProfileTilesPanel were not part of the tree PredictFocus is
        // allowed to search.
        private readonly Dictionary<Button, bool> _tileFocusableBackup =
            new Dictionary<Button, bool>();

        private NameEntryMode _nameEntryMode = NameEntryMode.None;
        private UserProfile _profileBeingRenamed;

        // 16A fix #2 (profile deletion): which profile the Delete
        // confirmation panel is currently asking about - null whenever
        // DeleteConfirmPanel is collapsed.
        private UserProfile _profileBeingDeleted;

        // Same "_active*" convention as SearchView's own _activeKeyboard -
        // non-null exactly while the on-screen keyboard overlay is open.
        private OnScreenKeyboardView _activeKeyboard;

        public ProfileSelectionView()
        {
            InitializeComponent();

            // 16A cosmetic label fix: KeyboardOverlay is this view's own
            // single, statically-declared on-screen keyboard instance (see
            // OpenKeyboard()/CloseKeyboard() remarks below) - it is only
            // ever used for the profile-name-entry context, never search,
            // so its submit key is relabeled once, here, for the lifetime
            // of the view. SearchView's own OnScreenKeyboardView instance
            // is untouched and keeps its default "Search" label. Submit
            // behaviour (SubmitRequested -> SaveNameButton's logic) is
            // completely unchanged - see OnScreenKeyboardView.SetSubmitLabel
            // remarks.
            KeyboardOverlay.SetSubmitLabel("Enter");
        }

        // =========================================================
        // INITIALIZE
        // =========================================================
        //
        // isSwitching only changes the subtitle text (see XAML) - the
        // rest of the view behaves identically for startup and
        // SWITCH PROFILE, per this view's own "one shared implementation"
        // design.
        public void Initialize(ProfileService profileService, bool isSwitching)
        {
            if (profileService == null)
            {
                throw new ArgumentNullException(nameof(profileService));
            }

            _profileService = profileService;

            SwitchingSubtitleText.Visibility =
                isSwitching ? Visibility.Visible : Visibility.Collapsed;

            BuildProfileTiles();
        }

        public void FocusFirstTile()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_selectButtons.Count > 0)
                {
                    FocusElement(_selectButtons[0]);
                }
            }), DispatcherPriority.Input);
        }

        // =========================================================
        // TILE CONSTRUCTION
        // =========================================================
        //
        // Built in code-behind rather than static XAML because the tile
        // count varies (1-5 profiles, plus an Add Profile tile) - same
        // convention OnScreenKeyboardView already uses for its own
        // generated key rows (see that file's own remarks).
        private void BuildProfileTiles()
        {
            ProfileTilesPanel.Children.Clear();
            _selectButtons.Clear();
            _focusRingForButton.Clear();
            _idleBrushForRing.Clear();

            IReadOnlyList<UserProfile> profiles = _profileService.GetProfiles();
            bool canDelete = _profileService.CanDeleteProfile();

            foreach (UserProfile profile in profiles)
            {
                ProfileTilesPanel.Children.Add(BuildProfileTile(profile, canDelete));
            }

            if (_profileService.CanCreateProfile())
            {
                ProfileTilesPanel.Children.Add(BuildAddProfileTile());
            }
        }

        private FrameworkElement BuildProfileTile(UserProfile profile, bool canDelete)
        {
            Color color = ColorFromKey(profile.ColorKey);

            var avatarBorder = new Border
            {
                Width = 110,
                Height = 110,
                CornerRadius = new CornerRadius(55),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = InitialFor(profile.Name),
                    Foreground = Brushes.White,
                    FontSize = 42,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var nameText = new TextBlock
            {
                Text = profile.Name,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130,
                TextAlignment = TextAlignment.Center
            };

            var selectContent = new StackPanel();
            selectContent.Children.Add(avatarBorder);
            selectContent.Children.Add(nameText);

            var selectButton = new Button
            {
                Style = (Style)FindResource("ProfileTileButtonStyle"),
                Content = selectContent,
                Tag = profile
            };

            selectButton.Click += ProfileSelectButton_Click;
            RegisterFocusRing(selectButton, avatarBorder);

            var renameButton = new Button
            {
                Content = "Edit name",
                Style = (Style)FindResource("ProfileRenameButtonStyle"),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = profile
            };

            renameButton.Click += ProfileRenameButton_Click;

            var column = new StackPanel
            {
                Margin = new Thickness(16, 0, 16, 0)
            };

            column.Children.Add(selectButton);
            column.Children.Add(renameButton);

            // 16A fix #2: Delete is its own row below Edit name, and only
            // exists at all when the service says a delete would still
            // leave at least one profile behind - "if only one profile
            // exists, Delete Profile must be disabled/hidden" (16A fix
            // spec). Hidden (not just disabled) so it never becomes a
            // dead/unfocusable tab stop for PredictFocus to route into.
            if (canDelete)
            {
                var deleteButton = new Button
                {
                    Content = "Delete profile",
                    Style = (Style)FindResource("ProfileDeleteButtonStyle"),
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = profile
                };

                deleteButton.Click += ProfileDeleteButton_Click;

                column.Children.Add(deleteButton);
            }

            _selectButtons.Add(selectButton);

            return column;
        }

        private FrameworkElement BuildAddProfileTile()
        {
            var plusBorder = new Border
            {
                Width = 110,
                Height = 110,
                CornerRadius = new CornerRadius(55),
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5F)),
                BorderThickness = new Thickness(2),
                Child = new TextBlock
                {
                    Text = "+",
                    Foreground = Brushes.White,
                    FontSize = 48,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var nameText = new TextBlock
            {
                Text = "Add Profile",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var content = new StackPanel();
            content.Children.Add(plusBorder);
            content.Children.Add(nameText);

            var addButton = new Button
            {
                Style = (Style)FindResource("ProfileTileButtonStyle"),
                Content = content
            };

            addButton.Click += AddProfileButton_Click;
            RegisterFocusRing(addButton, plusBorder);

            var column = new StackPanel
            {
                Margin = new Thickness(16, 0, 16, 0)
            };

            column.Children.Add(addButton);

            _selectButtons.Add(addButton);

            return column;
        }

        // =========================================================
        // FOCUS RING (16A fix #1 - profile focus indication)
        // =========================================================
        //
        // Same technique MediaCard.xaml.cs already uses for its own
        // CardBorder (PreviewGotKeyboardFocus/PreviewLostKeyboardFocus
        // toggling BorderBrush), extended to also cover mouse hover -
        // "Mouse hover/interaction should also provide appropriate visual
        // feedback" (16A fix spec). The ring stays white for both focus
        // and hover; BorderThickness is held constant at 3 the whole time
        // (only BorderBrush toggles Transparent/White) so gaining focus
        // never changes the tile's layout size the way MediaCard's 0-to-3
        // thickness change does.
        private void RegisterFocusRing(Button button, Border ring)
        {
            _focusRingForButton[button] = ring;
            _idleBrushForRing[ring] = ring.BorderBrush;

            button.PreviewGotKeyboardFocus += FocusRingButton_PreviewGotKeyboardFocus;
            button.PreviewLostKeyboardFocus += FocusRingButton_PreviewLostKeyboardFocus;
            button.MouseEnter += FocusRingButton_MouseEnter;
            button.MouseLeave += FocusRingButton_MouseLeave;
        }

        private void FocusRingButton_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            SetFocusRingVisible(sender as Button, true);
        }

        private void FocusRingButton_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var button = sender as Button;

            // Keyboard focus and mouse hover each independently earn the
            // ring - losing one must not hide it while the other still
            // applies (e.g. the mouse is still hovering when focus moves
            // away via a controller/keyboard press elsewhere).
            if (button != null && button.IsMouseOver)
            {
                return;
            }

            SetFocusRingVisible(button, false);
        }

        private void FocusRingButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SetFocusRingVisible(sender as Button, true);
        }

        private void FocusRingButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var button = sender as Button;

            if (button != null && button.IsKeyboardFocused)
            {
                return;
            }

            SetFocusRingVisible(button, false);
        }

        private void SetFocusRingVisible(Button button, bool visible)
        {
            if (button == null)
            {
                return;
            }

            Border ring;

            if (!_focusRingForButton.TryGetValue(button, out ring) || ring == null)
            {
                return;
            }

            if (visible)
            {
                ring.BorderBrush = Brushes.White;
                return;
            }

            Brush idleBrush;
            ring.BorderBrush = _idleBrushForRing.TryGetValue(ring, out idleBrush)
                ? idleBrush
                : Brushes.Transparent;
        }

        private static string InitialFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "?";
            }

            return name.Trim().Substring(0, 1).ToUpperInvariant();
        }

        private static Color ColorFromKey(string colorKey)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(colorKey))
                {
                    return (Color)ColorConverter.ConvertFromString(colorKey);
                }
            }
            catch
            {
                // Falls through to the default colour below.
            }

            return Color.FromRgb(0xE9, 0x1E, 0x63);
        }

        // =========================================================
        // SELECTION
        // =========================================================

        private void ProfileSelectButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var profile = button?.Tag as UserProfile;

            if (profile == null)
            {
                return;
            }

            RaiseProfileSelected(profile.Id);
        }

        private void RaiseProfileSelected(Guid profileId)
        {
            EventHandler<Guid> handler = ProfileSelected;
            handler?.Invoke(this, profileId);
        }

        // =========================================================
        // ADD / RENAME
        // =========================================================

        private void AddProfileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenNameEntry(NameEntryMode.Add, null);
        }

        private void ProfileRenameButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var profile = button?.Tag as UserProfile;

            if (profile == null)
            {
                return;
            }

            OpenNameEntry(NameEntryMode.Rename, profile);
        }

        // 16A fix #4 (on-screen keyboard, SearchView-pattern reuse):
        // opening name entry only focuses ProfileNameBox - it does NOT
        // show the keyboard. This matches SearchView exactly: the
        // keyboard stays Visibility.Collapsed (its XAML default) until
        // the existing, unchanged ActivateFocusedControl() path (A/Enter
        // while ProfileNameBox is focused) calls OpenKeyboard(), which is
        // the ONLY place visibility, _activeKeyboard, and FocusFirstKey()
        // all become true together - exactly mirroring
        // SearchView.ActivateFocusedElement's identical
        // "SearchBox.IsKeyboardFocusWithin -> OpenKeyboard()" trigger and
        // SearchView.OpenKeyboardButton_Click's plain, non-nested call.
        //
        // An earlier version of this method set KeyboardOverlay.Visibility
        // = Visibility.Visible directly here, outside OpenKeyboard(), to
        // make the keyboard visible for mouse discoverability the instant
        // name entry opened. That broke the "keyboard visible implies
        // _activeKeyboard is set" invariant CloseKeyboard()'s own
        // "if (_activeKeyboard == null) return;" guard depends on (the
        // same guard SearchView's CloseKeyboard() uses, safely, only
        // because SearchView never shows its keyboard without also
        // activating it) - a mouse user could see and click the keyboard's
        // Submit/Close controls while _activeKeyboard was still null, so
        // CloseKeyboard() silently no-op'd on every such click. Removing
        // the direct visibility assignment restores SearchView's real
        // invariant instead of adding a second guard on top of it.
        private void OpenNameEntry(NameEntryMode mode, UserProfile profile)
        {
            _nameEntryMode = mode;
            _profileBeingRenamed = profile;

            NameEntryTitleText.Text =
                mode == NameEntryMode.Add ? "Add Profile" : "Rename Profile";

            // "Editing an existing profile should pre-populate its current
            // name" (16A fix spec) - unchanged from before this fix, just
            // called out here since it is exactly what this line already
            // does for NameEntryMode.Rename.
            ProfileNameBox.Text = mode == NameEntryMode.Rename ? profile?.Name : string.Empty;
            ProfileNameBox.CaretIndex = ProfileNameBox.Text?.Length ?? 0;

            HideNameEntryError();

            SuspendTileFocus();

            NameEntryPanel.Visibility = Visibility.Visible;

            // 16A Issue 2 fix: focus is queued at DispatcherPriority.Input
            // rather than set synchronously, so it is applied after the
            // layout pass NameEntryPanel's just-triggered visibility
            // change needs to settle - setting it synchronously here was
            // unreliable for the same reason SearchView's own focus calls
            // (FocusSearchBox, FocusFirstKey) all go through
            // Dispatcher.BeginInvoke rather than being called inline.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ProfileNameBox.Focus();
                Keyboard.Focus(ProfileNameBox);
            }), DispatcherPriority.Input);

            // The on-screen keyboard is intentionally NOT shown here - see
            // the fix #4 remarks above OpenNameEntry. It stays
            // Visibility.Collapsed until OpenKeyboard() is actually called
            // (via ActivateFocusedControl(), or a future explicit mouse
            // "open keyboard" affordance, exactly as SearchView's keyboard
            // stays Collapsed until OpenKeyboardButton/controller A opens
            // it).
        }

        private void CloseNameEntry()
        {
            NameEntryPanel.Visibility = Visibility.Collapsed;
            CloseKeyboard();

            ResumeTileFocus();

            _nameEntryMode = NameEntryMode.None;
            _profileBeingRenamed = null;
        }

        // 16A active-keyboard-focus fix: see the _tileFocusableBackup
        // field remarks above for why this exists. Walks the exact
        // structure BuildProfileTile()/BuildAddProfileTile() build -
        // ProfileTilesPanel's children are each a "column" StackPanel,
        // and every Button directly under a column is a tile-row control
        // (select, Edit name, Delete profile) - so this needs no deeper
        // recursion than that, and stays correct automatically if a
        // profile is added/removed/renamed while name entry is closed,
        // since BuildProfileTiles() always runs before this view can be
        // reached again.
        private IEnumerable<Button> EnumerateTileButtons()
        {
            foreach (UIElement column in ProfileTilesPanel.Children)
            {
                if (!(column is Panel columnPanel))
                {
                    continue;
                }

                foreach (UIElement child in columnPanel.Children)
                {
                    if (child is Button button)
                    {
                        yield return button;
                    }
                }
            }
        }

        private void SuspendTileFocus()
        {
            _tileFocusableBackup.Clear();

            foreach (Button button in EnumerateTileButtons())
            {
                _tileFocusableBackup[button] = button.Focusable;
                button.Focusable = false;
            }
        }

        private void ResumeTileFocus()
        {
            foreach (KeyValuePair<Button, bool> entry in _tileFocusableBackup)
            {
                entry.Key.Focusable = entry.Value;
            }

            _tileFocusableBackup.Clear();
        }

        private void SaveNameButton_Click(object sender, RoutedEventArgs e)
        {
            TrySaveNameEntry();
        }

        private void CancelNameButton_Click(object sender, RoutedEventArgs e)
        {
            CloseNameEntry();
            FocusFirstTile();
        }

        private void TrySaveNameEntry()
        {
            string name = ProfileNameBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                ShowNameEntryError("Enter a name.");
                return;
            }

            bool success;

            if (_nameEntryMode == NameEntryMode.Add)
            {
                success = _profileService.CreateProfile(name) != null;

                if (!success)
                {
                    ShowNameEntryError("Could not add another profile.");
                    return;
                }
            }
            else if (_nameEntryMode == NameEntryMode.Rename && _profileBeingRenamed != null)
            {
                success = _profileService.RenameProfile(_profileBeingRenamed.Id, name);

                if (!success)
                {
                    ShowNameEntryError("Could not rename this profile.");
                    return;
                }
            }
            else
            {
                return;
            }

            CloseNameEntry();
            BuildProfileTiles();
            FocusFirstTile();
        }

        private void ShowNameEntryError(string message)
        {
            NameEntryErrorText.Text = message;
            NameEntryErrorText.Visibility = Visibility.Visible;
        }

        private void HideNameEntryError()
        {
            NameEntryErrorText.Text = string.Empty;
            NameEntryErrorText.Visibility = Visibility.Collapsed;
        }

        private void ProfileNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                TrySaveNameEntry();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseNameEntry();
                FocusFirstTile();
            }
        }

        // =========================================================
        // DELETE (16A fix #2 - profile deletion)
        // =========================================================
        //
        // A dedicated confirm panel, not a single click - "must require
        // explicit confirmation" / "do not allow accidental deletion from
        // a single controller/keyboard activation" (16A fix spec). The
        // ProfileDeleteButtonStyle button built per-tile in BuildProfileTile
        // is only ever present when ProfileService.CanDeleteProfile() was
        // true at the last BuildProfileTiles() call, so reaching this
        // handler at all already implies deletion is currently allowed;
        // ProfileService.DeleteProfile() re-checks the same rule regardless
        // (see that method's own remarks).

        private void ProfileDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var profile = button?.Tag as UserProfile;

            if (profile == null)
            {
                return;
            }

            OpenDeleteConfirm(profile);
        }

        private void OpenDeleteConfirm(UserProfile profile)
        {
            _profileBeingDeleted = profile;

            DeleteConfirmNameText.Text =
                string.Format(
                    "Delete the profile \"{0}\"? Its data cannot be recovered.",
                    profile.Name);

            DeleteConfirmPanel.Visibility = Visibility.Visible;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FocusElement(CancelDeleteButton);
            }), DispatcherPriority.Input);
        }

        private void CloseDeleteConfirm()
        {
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            _profileBeingDeleted = null;
        }

        private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            TryConfirmDelete();
        }

        private void CancelDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDeleteConfirm();
            FocusFirstTile();
        }

        // "After deletion, the profile list/focus state must update
        // correctly" / "If the deleted profile was the active profile,
        // safely route the user through profile selection and require
        // selection of another valid profile" (16A fix spec) - this method
        // never raises ProfileSelected itself, so ShowProfileSelectionAsync
        // (StremioVideoCenterView) keeps waiting exactly as it already does
        // for every other case where the user hasn't yet picked a profile
        // to enter; the rebuilt tile list (now missing the deleted profile)
        // is simply shown again, whether or not the deleted profile was the
        // active one.
        private void TryConfirmDelete()
        {
            if (_profileBeingDeleted == null)
            {
                CloseDeleteConfirm();
                return;
            }

            _profileService.DeleteProfile(_profileBeingDeleted.Id);

            CloseDeleteConfirm();
            BuildProfileTiles();
            FocusFirstTile();
        }

        // =========================================================
        // ON-SCREEN KEYBOARD (Slice 15 convention, reused as-is)
        // =========================================================

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

        // 16A keyboard-activation fix: the mouse/touch trigger
        // ProfileSelectionView was missing - a plain, non-nested call,
        // exactly mirroring SearchView.OpenKeyboardButton_Click. Before
        // this, OpenKeyboard() was reachable only via
        // ActivateFocusedControl() (controller A/Enter while ProfileNameBox
        // has focus), so a mouse-only user (no controller, no physical
        // keyboard) had no way to open the on-screen keyboard at all.
        private void OpenKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            OpenKeyboard();
        }

        // 16A Issue 1 fix: every path that closes the keyboard while
        // NameEntryPanel is still open must land focus on a specific,
        // known-good control inside the modal - never left to WPF's own
        // automatic focus reassignment, which was landing on the
        // background ProfileTilesPanel tiles (still Visibility.Visible,
        // just covered by this panel's higher Panel.ZIndex - the same
        // "still visible, just covered" shape DeleteConfirmPanel's own
        // remarks already call out). focusTarget lets each caller pick
        // the correct destination for how the keyboard closed - default
        // (ProfileNameBox) covers Cancel/dismiss, KeyboardOverlay_Submit
        // Requested passes SaveNameButton explicitly. This is the same
        // "CloseKeyboard always restores focus" shape SearchView.Close
        // Keyboard already uses (it always calls FocusSearchBox()) - the
        // only difference is this view has two different valid focus
        // destinations depending on how the keyboard closed, where
        // Search only ever has one.
        private void CloseKeyboard(FrameworkElement focusTarget = null)
        {
            if (_activeKeyboard == null)
            {
                return;
            }

            _activeKeyboard = null;
            KeyboardOverlay.Visibility = Visibility.Collapsed;

            FrameworkElement target = focusTarget ?? ProfileNameBox;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FocusElement(target);
            }), DispatcherPriority.Input);
        }

        private void KeyboardOverlay_CharacterEntered(object sender, string e)
        {
            InsertIntoNameBox(e);
        }

        private void KeyboardOverlay_BackspaceRequested(object sender, EventArgs e)
        {
            RemoveCharacterBeforeCaret();
        }

        private void KeyboardOverlay_ClearRequested(object sender, EventArgs e)
        {
            ProfileNameBox.Text = string.Empty;
            ProfileNameBox.CaretIndex = 0;
        }

        // 16A Issue 1 fix: submitting from the on-screen keyboard no
        // longer saves immediately - it closes the keyboard and moves
        // focus to Save, which the user then activates as its own,
        // separate step (Test Case A/B in the 16A fix spec: "Confirm
        // focus lands on Save. Press controller A/Enter. Profile is
        // created."). Save itself (SaveNameButton_Click) still calls
        // TrySaveNameEntry() exactly as before - this only removes the
        // keyboard's own Search/submit key from calling it a second,
        // earlier time. Physical-keyboard Enter typed directly into
        // ProfileNameBox (ProfileNameBox_KeyDown) is a separate path and
        // is unchanged - this is specific to the on-screen keyboard.
        private void KeyboardOverlay_SubmitRequested(object sender, EventArgs e)
        {
            CloseKeyboard(SaveNameButton);
        }

        private void KeyboardOverlay_CloseRequested(object sender, EventArgs e)
        {
            CloseKeyboard();
        }

        private void InsertIntoNameBox(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string current = ProfileNameBox.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(ProfileNameBox.CaretIndex, current.Length));

            ProfileNameBox.Text = current.Insert(caret, text);
            ProfileNameBox.CaretIndex = caret + text.Length;
        }

        private void RemoveCharacterBeforeCaret()
        {
            string current = ProfileNameBox.Text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(ProfileNameBox.CaretIndex, current.Length));

            if (caret <= 0 || current.Length == 0)
            {
                return;
            }

            ProfileNameBox.Text = current.Remove(caret - 1, 1);
            ProfileNameBox.CaretIndex = caret - 1;
        }

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================
        //
        // No Cancel/Back at the top level of this view (see XAML header
        // remarks) - Escape is only meaningful while the name-entry panel
        // is open, where it cancels that panel (ProfileNameBox_KeyDown
        // already handles it while the TextBox has focus; this handler
        // covers Escape pressed while a tile/button has focus instead).
        private void ProfileSelectionView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_activeKeyboard != null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (DeleteConfirmPanel.Visibility == Visibility.Visible)
                {
                    CloseDeleteConfirm();
                    FocusFirstTile();
                }
                else if (NameEntryPanel.Visibility == Visibility.Visible)
                {
                    CloseNameEntry();
                    FocusFirstTile();
                }

                e.Handled = true;
                return;
            }

            if (DeleteConfirmPanel.Visibility == Visibility.Visible)
            {
                // The Confirm/Cancel buttons handle their own Tab-order
                // navigation via WPF defaults while this panel is open -
                // their Left/Right toggle is handled directly in
                // HandleControllerInput instead (see that method's own
                // remarks on why PredictFocus is deliberately avoided
                // here) - nothing further to claim here.
                return;
            }

            // 16A directional-navigation fix: NameEntryPanel previously
            // fell into the same blanket "nothing further to claim here"
            // return as DeleteConfirmPanel above, which meant Up/Down/
            // Left/Right were never handled at all while this panel was
            // open (Tab/Shift+Tab still worked - that part of the old
            // comment was true - but arrows have no WPF-native equivalent,
            // so nothing ever moved focus off ProfileNameBox onto
            // OpenKeyboardButton/SaveNameButton/CancelNameButton). This
            // mirrors SearchView_PreviewKeyDown's own shape exactly:
            // ProfileNameBox still needs its own text-entry keys (Left/
            // Right for caret movement, digits, letters, Backspace, Enter
            // via ProfileNameBox_KeyDown, etc.) to reach the TextBox
            // normally. Right is intercepted only when the caret is
            // already at the end of the existing text (nowhere further
            // right for it to go), moving focus out to the keyboard
            // button instead of being a silent no-op - same rule, same
            // reasoning as SearchView's own MoveHorizontal guard. Left is
            // never intercepted here while the TextBox has focus:
            // ProfileNameBox is the leftmost stop in this chain, so Left
            // always stays caret movement (MoveNameEntryFocus below is
            // the equivalent, explicit-focus-target navigation for the
            // rest of this row/column - see its own remarks for why
            // PredictFocus/native directional focus is deliberately not
            // used here, same as SearchView's MoveHorizontal/MoveVertical).
            if (NameEntryPanel.Visibility == Visibility.Visible)
            {
                if (ProfileNameBox.IsKeyboardFocusWithin)
                {
                    if (e.Key == Key.Right &&
                        ProfileNameBox.CaretIndex >= (ProfileNameBox.Text?.Length ?? 0))
                    {
                        MoveNameEntryFocus(FocusNavigationDirection.Right);
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Down)
                    {
                        MoveNameEntryFocus(FocusNavigationDirection.Down);
                        e.Handled = true;
                    }

                    return;
                }

                switch (e.Key)
                {
                    case Key.Up:
                        MoveNameEntryFocus(FocusNavigationDirection.Up);
                        e.Handled = true;
                        break;

                    case Key.Down:
                        MoveNameEntryFocus(FocusNavigationDirection.Down);
                        e.Handled = true;
                        break;

                    case Key.Left:
                        MoveNameEntryFocus(FocusNavigationDirection.Left);
                        e.Handled = true;
                        break;

                    case Key.Right:
                        MoveNameEntryFocus(FocusNavigationDirection.Right);
                        e.Handled = true;
                        break;
                }

                return;
            }

            switch (e.Key)
            {
                case Key.Up:
                    MoveFocus(FocusNavigationDirection.Up);
                    e.Handled = true;
                    break;

                case Key.Down:
                    MoveFocus(FocusNavigationDirection.Down);
                    e.Handled = true;
                    break;

                case Key.Left:
                    MoveFocus(FocusNavigationDirection.Left);
                    e.Handled = true;
                    break;

                case Key.Right:
                    MoveFocus(FocusNavigationDirection.Right);
                    e.Handled = true;
                    break;
            }
        }

        // =========================================================
        // CONTROLLER INPUT
        // =========================================================

        public bool HandleControllerInput(ControllerInput button)
        {
            if (_activeKeyboard != null)
            {
                return _activeKeyboard.HandleControllerInput(button);
            }

            if (DeleteConfirmPanel.Visibility == Visibility.Visible)
            {
                switch (button)
                {
                    case ControllerInput.B:
                        CloseDeleteConfirm();
                        FocusFirstTile();
                        return true;

                    case ControllerInput.A:
                        return ActivateFocusedControl();

                    // The confirm dialog only ever has two buttons
                    // (Cancel/Delete) - toggling between them directly
                    // rather than going through the generic MoveFocus/
                    // PredictFocus path below avoids PredictFocus ever
                    // considering the profile tiles still sitting
                    // (Visibility.Visible, just covered by this panel's
                    // higher Panel.ZIndex) underneath - the same risk
                    // NameEntryPanel's own MoveNameEntryFocus avoids by
                    // using explicit focus targets instead of PredictFocus
                    // (see that method's own remarks).
                    case ControllerInput.DPadLeft:
                    case ControllerInput.DPadRight:
                        FocusElement(
                            CancelDeleteButton.IsKeyboardFocused
                                ? ConfirmDeleteButton
                                : CancelDeleteButton);
                        return true;
                }

                return false;
            }

            if (NameEntryPanel.Visibility == Visibility.Visible)
            {
                switch (button)
                {
                    case ControllerInput.B:
                        CloseNameEntry();
                        FocusFirstTile();
                        return true;

                    case ControllerInput.A:
                        return ActivateFocusedControl();

                    // 16A directional-navigation fix: same
                    // MoveNameEntryFocus chain the physical-keyboard path
                    // above (ProfileSelectionView_PreviewKeyDown) now
                    // uses - one shared method for both input sources,
                    // not two separate implementations, mirroring
                    // SearchView's own HandleControllerInput/PreviewKeyDown
                    // both calling MoveHorizontal/MoveVertical.
                    case ControllerInput.DPadLeft:
                        MoveNameEntryFocus(FocusNavigationDirection.Left);
                        return true;

                    case ControllerInput.DPadRight:
                        MoveNameEntryFocus(FocusNavigationDirection.Right);
                        return true;

                    case ControllerInput.DPadUp:
                        MoveNameEntryFocus(FocusNavigationDirection.Up);
                        return true;

                    case ControllerInput.DPadDown:
                        MoveNameEntryFocus(FocusNavigationDirection.Down);
                        return true;
                }

                return false;
            }

            switch (button)
            {
                // No Cancel/Back at the top level - see XAML header
                // remarks. B is consumed (no-op) so it can never close
                // this overlay or fall through to closing the whole
                // Video Center window.
                case ControllerInput.B:
                    return true;

                case ControllerInput.DPadUp:
                    MoveFocus(FocusNavigationDirection.Up);
                    return true;

                case ControllerInput.DPadDown:
                    MoveFocus(FocusNavigationDirection.Down);
                    return true;

                case ControllerInput.DPadLeft:
                    MoveFocus(FocusNavigationDirection.Left);
                    return true;

                case ControllerInput.DPadRight:
                    MoveFocus(FocusNavigationDirection.Right);
                    return true;

                case ControllerInput.A:
                    return ActivateFocusedControl();
            }

            return false;
        }

        private bool ActivateFocusedControl()
        {
            if (ProfileNameBox.IsKeyboardFocusWithin)
            {
                OpenKeyboard();
                return true;
            }

            if (Keyboard.FocusedElement is ButtonBase focusedButton)
            {
                focusedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return true;
            }

            return false;
        }

        // =========================================================
        // FOCUS MOVEMENT
        // =========================================================
        //
        // WPF's own directional focus prediction, same technique (and
        // same reasoning) as OnScreenKeyboardView.MoveFocus - this
        // control's layout (a row of tile columns, each with a select
        // button above a rename button) is exactly the kind of uneven
        // grid that a hand-built index table handles awkwardly, while
        // PredictFocus handles it directly.
        private void MoveFocus(FocusNavigationDirection direction)
        {
            var current = Keyboard.FocusedElement as UIElement;

            if (current == null)
            {
                FocusFirstTile();
                return;
            }

            var next = current.PredictFocus(direction) as UIElement;

            if (next == null || !IsDescendantOf(next, this))
            {
                return;
            }

            var focusable = next as FrameworkElement;

            if (focusable != null)
            {
                FocusElement(focusable);
            }
        }

        // 16A directional-navigation fix: NameEntryPanel's own focus chain
        // (ProfileNameBox / OpenKeyboardButton / SaveNameButton /
        // CancelNameButton), explicit-target-based rather than
        // PredictFocus-driven - the same reasoning SearchView.MoveHorizontal/
        // MoveVertical already documents for its own SearchBox/
        // OpenKeyboardButton/SearchButton row: WPF's PredictFocus has no
        // way to know this row is meant to be treated as an ordered chain
        // rather than plain 2D layout, and (per MoveFocus's own remarks
        // just above) could walk PredictFocus out to the covered tile grid
        // underneath this modal, which is exactly the risk this panel has
        // avoided since the early-return this method replaces was written.
        // One shared method for both physical-keyboard (PreviewKeyDown)
        // and controller (HandleControllerInput) callers, not two separate
        // implementations - mirrors SearchView's own MoveHorizontal/
        // MoveVertical being the single shared implementation for both of
        // its own callers.
        //
        // Layout (see ProfileSelectionView.xaml): row 1 is ProfileNameBox
        // (wide) beside OpenKeyboardButton; row 2, directly below, is
        // SaveNameButton beside CancelNameButton. Right/Left walk the full
        // four-stop chain in visual left-to-right order; Down/Up cross
        // between the two rows, landing on the nearest sensible stop in
        // the other row rather than attempting per-column alignment the
        // layout doesn't actually have.
        private void MoveNameEntryFocus(FocusNavigationDirection direction)
        {
            if (ProfileNameBox.IsKeyboardFocusWithin)
            {
                if (direction == FocusNavigationDirection.Right)
                {
                    FocusElement(OpenKeyboardButton);
                }
                else if (direction == FocusNavigationDirection.Down)
                {
                    FocusElement(SaveNameButton);
                }

                // Left: ProfileNameBox is the leftmost stop in this chain.
                // For a physical Left press this method is never even
                // reached (see the IsKeyboardFocusWithin guard in
                // ProfileSelectionView_PreviewKeyDown - Left always stays
                // ordinary caret movement there, same as SearchView's own
                // "Left is never intercepted" comment); a controller
                // DPadLeft has no caret to move, so it is simply a no-op
                // here, the same clamp-not-wrap convention SearchView.
                // MoveHorizontal's own SearchBox branch already uses for
                // direction < 0.
                // Up: no-op - ProfileNameBox is the topmost stop.
                return;
            }

            if (OpenKeyboardButton.IsKeyboardFocused)
            {
                if (direction == FocusNavigationDirection.Right)
                {
                    FocusElement(SaveNameButton);
                }
                else if (direction == FocusNavigationDirection.Left)
                {
                    FocusElement(ProfileNameBox);
                }
                else if (direction == FocusNavigationDirection.Down)
                {
                    FocusElement(SaveNameButton);
                }

                // Up: no-op - top row.
                return;
            }

            if (SaveNameButton.IsKeyboardFocused)
            {
                if (direction == FocusNavigationDirection.Right)
                {
                    FocusElement(CancelNameButton);
                }
                else if (direction == FocusNavigationDirection.Left)
                {
                    FocusElement(OpenKeyboardButton);
                }
                else if (direction == FocusNavigationDirection.Up)
                {
                    FocusElement(ProfileNameBox);
                }

                // Down: no-op - bottom row.
                return;
            }

            if (CancelNameButton.IsKeyboardFocused)
            {
                if (direction == FocusNavigationDirection.Left)
                {
                    FocusElement(SaveNameButton);
                }
                else if (direction == FocusNavigationDirection.Up)
                {
                    FocusElement(ProfileNameBox);
                }

                // Right: no-op - rightmost stop.
                // Down: no-op - bottom row.
                return;
            }
        }

        private static bool IsDescendantOf(DependencyObject candidate, DependencyObject ancestor)
        {
            DependencyObject current = candidate;

            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

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
