using Playnite.SDK.Events;
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
    // ON-SCREEN KEYBOARD VIEW (Slice 15 - on-screen keyboard task)
    // =============================================================
    //
    // Reusable virtual keyboard control. It has no knowledge of any
    // specific text field - it only raises events for what the user did
    // (a character key, Backspace, Clear, Submit, or Close/Back) and lets
    // the host view (SearchView, this slice's only caller) apply that to
    // whatever TextBox it owns. See SearchView.xaml.cs's own remarks on
    // how it wires these events into SearchBox.
    //
    // Row/key building is done in code-behind (BuildKeyRows), matching
    // this project's existing convention for content that's naturally a
    // small generated grid rather than hand-authored XAML (compare
    // EpisodeBrowserView.BuildSeasonButton/BuildEpisodeButton).
    public partial class OnScreenKeyboardView : UserControl
    {
        // Row contents (lowercase letters shown in Tag/character; upper
        // rendered when Shift is toggled on). Deliberately a plain QWERTY
        // layout - "do not over-engineer the visual design" per this
        // slice's own instructions.
        private static readonly string[] NumberRowKeys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
        private static readonly string[] TopLetterRowKeys = { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" };
        private static readonly string[] MiddleLetterRowKeys = { "a", "s", "d", "f", "g", "h", "j", "k", "l" };
        private static readonly string[] BottomLetterRowKeys = { "z", "x", "c", "v", "b", "n", "m", ".", "-" };

        private static readonly SolidColorBrush ShiftActiveBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63));
        private static readonly SolidColorBrush ShiftInactiveBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F));

        private readonly List<Button> _letterButtons = new List<Button>();
        private Button _shiftButton;
        private bool _shiftActive;

        public event EventHandler<string> CharacterEntered;
        public event EventHandler BackspaceRequested;
        public event EventHandler ClearRequested;
        public event EventHandler SubmitRequested;
        public event EventHandler CloseRequested;

        public OnScreenKeyboardView()
        {
            InitializeComponent();

            BuildKeyRows();
        }

        // =========================================================
        // KEY ROW CONSTRUCTION
        // =========================================================

        private void BuildKeyRows()
        {
            foreach (string key in NumberRowKeys)
            {
                NumberRow.Children.Add(BuildCharacterKey(key, isLetter: false));
            }

            foreach (string key in TopLetterRowKeys)
            {
                TopLetterRow.Children.Add(BuildCharacterKey(key, isLetter: true));
            }

            foreach (string key in MiddleLetterRowKeys)
            {
                MiddleLetterRow.Children.Add(BuildCharacterKey(key, isLetter: true));
            }

            // Shift lives at the start of the bottom letter row and
            // Backspace at the end, mirroring a physical keyboard's own
            // layout so the control reads immediately as "a keyboard"
            // rather than an unfamiliar grid.
            _shiftButton = new Button
            {
                Content = "⇧ Shift",
                Style = (Style)FindResource("WideKeyButtonStyle"),
                MinWidth = 90,
                Background = ShiftInactiveBrush,
                Tag = "action:shift"
            };
            _shiftButton.Click += ActionKey_Click;
            BottomLetterRow.Children.Add(_shiftButton);

            foreach (string key in BottomLetterRowKeys)
            {
                bool isLetter = key.Length == 1 && char.IsLetter(key[0]);
                BottomLetterRow.Children.Add(BuildCharacterKey(key, isLetter));
            }

            Button backspaceButton = new Button
            {
                Content = "⌫",
                Style = (Style)FindResource("WideKeyButtonStyle"),
                MinWidth = 90,
                ToolTip = "Backspace",
                Tag = "action:backspace"
            };
            backspaceButton.Click += ActionKey_Click;
            BottomLetterRow.Children.Add(backspaceButton);
        }

        private Button BuildCharacterKey(string lowerCaseValue, bool isLetter)
        {
            var button = new Button
            {
                Content = lowerCaseValue,
                Style = (Style)FindResource("KeyButtonStyle"),
                Tag = lowerCaseValue
            };

            button.Click += CharacterKey_Click;

            if (isLetter)
            {
                _letterButtons.Add(button);
            }

            return button;
        }

        // =========================================================
        // KEY ACTIVATION
        // =========================================================

        private void CharacterKey_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string baseValue = button?.Tag as string;

            if (string.IsNullOrEmpty(baseValue))
            {
                return;
            }

            string value = _shiftActive ? baseValue.ToUpperInvariant() : baseValue;

            RaiseCharacterEntered(value);
        }

        private void ActionKey_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string action = button?.Tag as string;

            switch (action)
            {
                case "action:space":
                    RaiseCharacterEntered(" ");
                    break;

                case "action:backspace":
                    RaiseBackspaceRequested();
                    break;

                case "action:clear":
                    RaiseClearRequested();
                    break;

                case "action:submit":
                    RaiseSubmitRequested();
                    break;

                case "action:shift":
                    ToggleShift();
                    break;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseCloseRequested();
        }

        // Toggle Caps, not a one-shot Shift-then-revert - simplest reliable
        // state machine for a D-pad-driven keyboard (see this file's type
        // remarks / the XAML header comment for why this slice keeps Shift
        // deliberately simple). The Shift key's own Tag stays a fixed
        // "action:shift" always (ActionKey_Click's dispatch depends on
        // it); the toggled-on look is driven by directly setting the
        // button's Background, not by a style trigger keyed off Tag, so
        // dispatch and visual state never fight over the same property.
        private void ToggleShift()
        {
            _shiftActive = !_shiftActive;

            foreach (Button letterButton in _letterButtons)
            {
                string baseValue = letterButton.Tag as string;

                if (string.IsNullOrEmpty(baseValue))
                {
                    continue;
                }

                letterButton.Content = _shiftActive
                    ? baseValue.ToUpperInvariant()
                    : baseValue;
            }

            if (_shiftButton != null)
            {
                _shiftButton.Background = _shiftActive ? ShiftActiveBrush : ShiftInactiveBrush;
            }
        }

        // =========================================================
        // EVENT RAISERS
        // =========================================================

        private void RaiseCharacterEntered(string value)
        {
            CharacterEntered?.Invoke(this, value);
        }

        private void RaiseBackspaceRequested()
        {
            BackspaceRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseClearRequested()
        {
            ClearRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseSubmitRequested()
        {
            SubmitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseCloseRequested()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // =========================================================
        // FOCUS ACQUISITION
        // =========================================================

        public void FocusFirstKey()
        {
            Dispatcher.BeginInvoke(
                new Action(() => FocusElement(FindFirstLetterButton())),
                DispatcherPriority.Input);
        }

        private Button FindFirstLetterButton()
        {
            return _letterButtons.Count > 0 ? _letterButtons[0] : null;
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

        // =========================================================
        // KEYBOARD INPUT
        // =========================================================

        private void OnScreenKeyboardView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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

                case Key.Enter:
                    ActivateFocused();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    RaiseCloseRequested();
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
                    ActivateFocused();
                    return true;

                case ControllerInput.B:
                    RaiseCloseRequested();
                    return true;
            }

            return false;
        }

        // =========================================================
        // FOCUS MOVEMENT
        // =========================================================
        //
        // Uses WPF's own directional focus prediction rather than a
        // hand-built row/column table - see the XAML file's header remark
        // for why that fits this control's uneven row lengths better than
        // reimplementing grid indexing. PredictFocus scopes its search to
        // this control's own visual subtree naturally (it only considers
        // focusable descendants it can actually reach), so it never moves
        // focus out to SearchView's own controls (e.g. the search box)
        // while the keyboard is open.

        private void MoveFocus(FocusNavigationDirection direction)
        {
            var current = Keyboard.FocusedElement as UIElement;

            if (current == null)
            {
                FocusFirstKey();
                return;
            }

            var next = current.PredictFocus(direction) as UIElement;

            // PredictFocus can walk outside this control's own subtree
            // (e.g. up and out to SearchView's search box) once it runs
            // out of candidates in this direction within the keyboard -
            // that would silently steal focus out from under an overlay
            // that's supposed to own all input while open, so any target
            // that is not actually a descendant of this control is
            // treated the same as "no further candidate" (clamp, not
            // wrap - the same convention MediaSection.MoveHorizontal
            // already uses at its own edges).
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

        private void ActivateFocused()
        {
            var focused = Keyboard.FocusedElement as ButtonBase;

            focused?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
    }
}
