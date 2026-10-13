using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenterControls.Player.Menus
{
    public sealed class SubtitleOptionChosenEventArgs : EventArgs
    {
        public SubtitleOption Option { get; private set; }

        public SubtitleOptionChosenEventArgs(SubtitleOption option)
        {
            Option = option;
        }
    }

    // Where the external (addon) side of the page is.
    public enum ExternalSubtitleState
    {
        NotRequested,
        Loading,
        Ready,
        Failed
    }

    // =============================================================
    // SUBTITLE PAGE (Slice 20 Stage 3)
    // =============================================================
    //
    // The full-page, language-first subtitle selector. It is a plain
    // control that MpvPlayer places in its existing MenuHost (inside the
    // owned controls-overlay window) exactly where the old Subtitles menu
    // went, and pushes as a PlayerFocusController menu scope - so Back,
    // focus return and the pause/restore guard are the same machinery the
    // Audio/Subtitles menus already used. No window of its own.
    //
    //   Top:    title + Back button
    //   Left:   "Off" and one button per language
    //   Right:  the options for the active language (embedded and
    //           external, tagged), plus a status line for the external side
    //   Bottom: subtitle delay (carried over from the old menu)
    //
    // It holds no mpv, network or activation logic: MpvPlayer feeds it
    // state (SetTracks / SetExternal...) and acts on its events. Options
    // are built by SubtitleCatalog; external URLs never reach any text on
    // this page.
    public sealed class SubtitlePage : UserControl
    {
        public event EventHandler<SubtitleOptionChosenEventArgs> OptionChosen;
        public event EventHandler OffChosen;
        public event EventHandler BackRequested;
        public event EventHandler<double> DelayStepRequested;
        public event EventHandler DelayResetRequested;

        private static Style _buttonStyle;

        private readonly Button _backButton;
        private readonly Button _offButton;
        private readonly StackPanel _languageList = new StackPanel();
        private readonly StackPanel _optionList = new StackPanel();
        private readonly ScrollViewer _languageScroll;
        private readonly ScrollViewer _optionScroll;
        private readonly TextBlock _languageTitle;
        private readonly TextBlock _statusText;
        private readonly Button _delayMinus;
        private readonly Button _delayPlus;
        private readonly Button _delayReset;
        private readonly TextBlock _delayValue;

        private readonly List<Button> _languageButtons = new List<Button>();
        private readonly List<Button> _optionButtons = new List<Button>();

        private IReadOnlyList<MpvTrackInfo> _tracks = new List<MpvTrackInfo>();
        private int? _selectedTrackId;
        private bool _tracksLoaded;
        private IDictionary<string, int> _loadedExternal = new Dictionary<string, int>();

        private ExternalSubtitleState _externalState = ExternalSubtitleState.NotRequested;
        private SubtitleDiscoveryResult _externalResult;

        private List<SubtitleLanguageGroup> _groups = new List<SubtitleLanguageGroup>();
        private string _activeLanguageKey;

        private bool _busy;
        private string _busyText;
        private string _transientText;

        public SubtitlePage()
        {
            Focusable = false;
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x0E, 0x0E, 0x10));

            Grid root = new Grid { Margin = new Thickness(48, 32, 48, 28) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- header ---------------------------------------------------
            DockPanel header = new DockPanel { Margin = new Thickness(0, 0, 0, 18), LastChildFill = false };

            _backButton = NewButton("Back", 100);
            _backButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            _backButton.Click += (s, e) => Raise(BackRequested);
            DockPanel.SetDock(_backButton, Dock.Right);
            header.Children.Add(_backButton);

            header.Children.Add(new TextBlock
            {
                Text = "Subtitles",
                Foreground = Brushes.White,
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // --- body -----------------------------------------------------
            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: Off + languages.
            StackPanel left = new StackPanel();
            _offButton = NewButton("Off", 0);
            _offButton.Click += (s, e) => { if (!_busy) Raise(OffChosen); };
            left.Children.Add(_offButton);
            left.Children.Add(_languageList);

            _languageScroll = NewScroll(left);
            Grid.SetColumn(_languageScroll, 0);
            body.Children.Add(_languageScroll);

            // Right: language title, options, status.
            Grid right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _languageTitle = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 6, 10)
            };
            Grid.SetRow(_languageTitle, 0);
            right.Children.Add(_languageTitle);

            _optionScroll = NewScroll(_optionList);
            Grid.SetRow(_optionScroll, 1);
            right.Children.Add(_optionScroll);

            _statusText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 10, 6, 0),
                Visibility = Visibility.Collapsed,
                Focusable = false,
                IsHitTestVisible = false
            };
            Grid.SetRow(_statusText, 2);
            right.Children.Add(_statusText);

            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // --- footer: delay --------------------------------------------
            StackPanel footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0)
            };

            footer.Children.Add(new TextBlock
            {
                Text = "Subtitle delay",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            });

            _delayMinus = NewButton("−", 52);
            _delayMinus.ToolTip = "Subtitles earlier";
            _delayMinus.HorizontalContentAlignment = HorizontalAlignment.Center;
            _delayMinus.Click += (s, e) => RaiseDelay(-SubtitleDelay.StepSeconds);

            _delayValue = new TextBlock
            {
                Text = SubtitleDelay.Format(0),
                Foreground = Brushes.White,
                FontSize = 16,
                MinWidth = 70,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                IsHitTestVisible = false
            };

            _delayPlus = NewButton("+", 52);
            _delayPlus.ToolTip = "Subtitles later";
            _delayPlus.HorizontalContentAlignment = HorizontalAlignment.Center;
            _delayPlus.Click += (s, e) => RaiseDelay(SubtitleDelay.StepSeconds);

            _delayReset = NewButton("Reset to 0.0s", 0);
            _delayReset.Margin = new Thickness(14, 0, 0, 0);
            _delayReset.Click += (s, e) => Raise(DelayResetRequested);

            footer.Children.Add(_delayMinus);
            footer.Children.Add(_delayValue);
            footer.Children.Add(_delayPlus);
            footer.Children.Add(_delayReset);

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
        }

        // ---------------------------------------------------------------
        // STATE FEEDING (called by MpvPlayer)
        // ---------------------------------------------------------------

        // A new open: forget everything from the previous one.
        public void Reset()
        {
            _tracks = new List<MpvTrackInfo>();
            _selectedTrackId = null;
            _tracksLoaded = false;
            _loadedExternal = new Dictionary<string, int>();
            _externalState = ExternalSubtitleState.NotRequested;
            _externalResult = null;
            _groups = new List<SubtitleLanguageGroup>();
            _activeLanguageKey = null;
            _busy = false;
            _busyText = null;
            _transientText = null;
        }

        public void SetTracks(IReadOnlyList<MpvTrackInfo> tracks, int? selectedTrackId, bool tracksLoaded)
        {
            _tracks = tracks ?? new List<MpvTrackInfo>();
            _selectedTrackId = selectedTrackId;
            _tracksLoaded = tracksLoaded;
        }

        public void SetLoadedExternal(IDictionary<string, int> loadedExternal)
        {
            _loadedExternal = loadedExternal ?? new Dictionary<string, int>();
        }

        public void SetExternalLoading()
        {
            _externalState = ExternalSubtitleState.Loading;
            _externalResult = null;
        }

        public void SetExternalResult(SubtitleDiscoveryResult result)
        {
            _externalState = result != null ? ExternalSubtitleState.Ready : ExternalSubtitleState.Failed;
            _externalResult = result;
        }

        public void SetExternalFailed()
        {
            _externalState = ExternalSubtitleState.Failed;
            _externalResult = null;
        }

        // While an external subtitle is being added: the status line says
        // so and further picks are ignored.
        public void SetBusy(string text)
        {
            _busy = true;
            _busyText = text;
            _transientText = null;
            ApplyStatus();
        }

        // After a failed pick: the page stays open with a short notice.
        public void ClearBusy(string transientNotice)
        {
            _busy = false;
            _busyText = null;
            _transientText = transientNotice;
            ApplyStatus();
        }

        public void UpdateDelay(double seconds)
        {
            _delayValue.Text = SubtitleDelay.Format(seconds);
        }

        public bool IsBusy
        {
            get { return _busy; }
        }

        // Rebuilds the visible lists from the state fed so far, keeping
        // keyboard focus on the same item when it is still there. Safe to
        // call repeatedly (new tracks, external results, a loaded subtitle).
        public void Refresh()
        {
            string focusedIdentity = FocusedIdentity();

            _groups = SubtitleCatalog.Build(
                _tracks,
                _selectedTrackId,
                _externalResult != null ? _externalResult.Subtitles : null,
                _loadedExternal);

            EnsureActiveLanguage();
            RebuildLanguageColumn();
            RebuildOptions();
            ApplyStatus();

            _offButton.Content = LanguageContent(
                "Off",
                _tracksLoaded && !_selectedTrackId.HasValue);

            if (focusedIdentity != null)
                RestoreFocus(focusedIdentity);
        }

        // The focus scope MpvPlayer pushes. All movement is handled here
        // (two columns do not fit the controller's flat-list scope), so the
        // group itself only names where focus first lands.
        public FocusGroup CreateFocusGroup()
        {
            FocusGroup group = new FocusGroup();
            group.InitialElement = FindInitialElement();
            group.HandleHorizontal = HandleHorizontal;
            group.HandleVertical = HandleVertical;
            return group;
        }

        // ---------------------------------------------------------------
        // BUILDING
        // ---------------------------------------------------------------

        private void EnsureActiveLanguage()
        {
            if (_groups.Count == 0)
            {
                _activeLanguageKey = null;
                return;
            }

            if (_activeLanguageKey != null && _groups.Any(g => g.Key == _activeLanguageKey))
                return;

            SubtitleLanguageGroup chosen =
                _groups.FirstOrDefault(g => g.HasSelected) ??
                _groups.FirstOrDefault(g => g.Key == "english") ??
                _groups[0];

            _activeLanguageKey = chosen.Key;
        }

        private SubtitleLanguageGroup ActiveGroup()
        {
            return _groups.FirstOrDefault(g => g.Key == _activeLanguageKey);
        }

        private void RebuildLanguageColumn()
        {
            _languageList.Children.Clear();
            _languageButtons.Clear();

            foreach (SubtitleLanguageGroup group in _groups)
            {
                SubtitleLanguageGroup captured = group;
                bool active = captured.Key == _activeLanguageKey;

                Button button = NewButton(null, 0);
                button.Content = LanguageContent(captured.Display, captured.HasSelected);
                button.Tag = "lang:" + captured.Key;
                button.Background = active ? SelectedBrush : IdleBrush;

                // Browsing the list previews that language's options.
                button.GotKeyboardFocus += (s, e) => ActivateLanguage(captured.Key, false);
                button.Click += (s, e) =>
                {
                    ActivateLanguage(captured.Key, false);
                    FocusFirstOption();
                };

                _languageList.Children.Add(button);
                _languageButtons.Add(button);
            }
        }

        private void ActivateLanguage(string key, bool force)
        {
            if (!force && key == _activeLanguageKey)
                return;

            _activeLanguageKey = key;

            foreach (Button button in _languageButtons)
            {
                button.Background = (string)button.Tag == "lang:" + key ? SelectedBrush : IdleBrush;
            }

            RebuildOptions();
            ApplyStatus();
        }

        private void RebuildOptions()
        {
            _optionList.Children.Clear();
            _optionButtons.Clear();

            SubtitleLanguageGroup group = ActiveGroup();

            _languageTitle.Text = group != null ? group.Display : string.Empty;

            if (group == null)
                return;

            foreach (SubtitleOption option in group.Options)
            {
                SubtitleOption captured = option;

                StackPanel content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = (captured.IsSelected ? "● " : "○ ") + captured.Label,
                    Foreground = Brushes.White,
                    FontSize = 16,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                content.Children.Add(new TextBlock
                {
                    Text = TagFor(captured),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                    FontSize = 12,
                    Margin = new Thickness(18, 2, 0, 0)
                });

                Button button = NewButton(null, 0);
                button.Content = content;
                button.Tag = captured;
                button.Background = captured.IsSelected ? SelectedBrush : IdleBrush;
                button.Click += (s, e) =>
                {
                    if (!_busy)
                        OptionChosen?.Invoke(this, new SubtitleOptionChosenEventArgs(captured));
                };

                _optionList.Children.Add(button);
                _optionButtons.Add(button);
            }
        }

        private static string TagFor(SubtitleOption option)
        {
            if (option.Kind == SubtitleOptionKind.Embedded)
                return "Embedded";

            return option.TrackId.HasValue ? "External · loaded" : "External";
        }

        // ---------------------------------------------------------------
        // STATUS LINE
        // ---------------------------------------------------------------

        private void ApplyStatus()
        {
            string text = _busy ? _busyText : ComposeStatus();

            if (!_busy && !string.IsNullOrEmpty(_transientText))
                text = string.IsNullOrEmpty(text) ? _transientText : _transientText + " " + text;

            _statusText.Text = text ?? string.Empty;
            _statusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private string ComposeStatus()
        {
            SubtitleLanguageGroup group = ActiveGroup();
            List<string> parts = new List<string>();

            if (!_tracksLoaded)
                parts.Add("Loading embedded subtitles…");

            string external = ExternalStatus(group);

            if (!string.IsNullOrEmpty(external))
                parts.Add(external);

            if (_tracksLoaded && _groups.Count == 0 && _externalState == ExternalSubtitleState.Ready &&
                parts.Count == 0)
                parts.Add("No subtitles are available for this title.");

            return string.Join(" ", parts);
        }

        // Fixed wording only. Never an exception message, provider
        // response or address.
        private string ExternalStatus(SubtitleLanguageGroup group)
        {
            switch (_externalState)
            {
                case ExternalSubtitleState.Loading:
                    return "Loading external subtitles…";

                case ExternalSubtitleState.Failed:
                    return "External subtitles couldn’t be loaded.";

                case ExternalSubtitleState.Ready:
                    break;

                default:
                    return null;
            }

            SubtitleDiscoveryResult result = _externalResult;

            if (result == null || result.ContentUnidentified)
                return "External subtitles aren’t available for this title.";

            if (result.PerAddon == null || result.PerAddon.Count == 0)
                return "No subtitle addon is set up.";

            List<AddonQueryResult<AddonSubtitle>> asked = result.PerAddon
                .Where(r => r.Outcome != AddonQueryOutcome.Skipped && r.Outcome != AddonQueryOutcome.Cancelled)
                .ToList();

            if (asked.Count == 0)
                return "External subtitles aren’t available for this title.";

            bool anyAnswered = asked.Any(r =>
                r.Outcome == AddonQueryOutcome.SuccessWithResults ||
                r.Outcome == AddonQueryOutcome.SuccessNoResults);
            bool anyFailed = asked.Any(r => r.Outcome == AddonQueryOutcome.Failed);
            bool anyTimedOut = asked.Any(r => r.Outcome == AddonQueryOutcome.TimedOut);

            if (!anyAnswered)
            {
                return anyTimedOut && !anyFailed
                    ? "The subtitle provider took too long to respond."
                    : "External subtitles couldn’t be loaded.";
            }

            string partial = anyFailed || anyTimedOut
                ? "Some subtitle providers didn’t respond."
                : null;

            bool hasExternalHere = group != null &&
                group.Options.Any(o => o.Kind == SubtitleOptionKind.External);

            if (hasExternalHere)
                return partial;

            string none = result.Subtitles.Count > 0 && group != null
                ? "No external subtitles found for " + group.Display + "."
                : "No external subtitles found for this title.";

            return partial == null ? none : none + " " + partial;
        }

        // ---------------------------------------------------------------
        // NAVIGATION
        // ---------------------------------------------------------------

        private UIElement FindInitialElement()
        {
            SubtitleOption selected = _groups
                .SelectMany(g => g.Options)
                .FirstOrDefault(o => o.IsSelected);

            if (selected != null)
            {
                Button button = _optionButtons.FirstOrDefault(b => ReferenceEquals(b.Tag, selected));
                if (button != null)
                    return button;
            }

            Button language = _languageButtons.FirstOrDefault(b => (string)b.Tag == "lang:" + _activeLanguageKey);

            if (language != null)
                return language;

            return _offButton;
        }

        private bool HandleVertical(int direction)
        {
            IInputElement focused = Keyboard.FocusedElement;

            if (ReferenceEquals(focused, _backButton))
            {
                if (direction > 0)
                    FocusElement(_offButton);
                return true;
            }

            if (ReferenceEquals(focused, _delayMinus) || ReferenceEquals(focused, _delayPlus) ||
                ReferenceEquals(focused, _delayReset))
            {
                if (direction < 0)
                {
                    if (_optionButtons.Count > 0)
                        FocusElement(_optionButtons[_optionButtons.Count - 1]);
                    else
                        FocusElement(_offButton);
                }
                return true;
            }

            List<UIElement> column = LanguageColumn();
            int index = IndexOf(column, focused);

            if (index >= 0)
            {
                MoveInColumn(column, index, direction);
                return true;
            }

            index = _optionButtons.FindIndex(b => ReferenceEquals(b, focused));

            if (index >= 0)
            {
                List<UIElement> options = _optionButtons.Cast<UIElement>().ToList();
                MoveInColumn(options, index, direction);
                return true;
            }

            FocusElement(FindInitialElement());
            return true;
        }

        private bool HandleHorizontal(int direction)
        {
            IInputElement focused = Keyboard.FocusedElement;

            if (ReferenceEquals(focused, _delayMinus) || ReferenceEquals(focused, _delayPlus) ||
                ReferenceEquals(focused, _delayReset))
            {
                UIElement[] row = { _delayMinus, _delayPlus, _delayReset };
                int at = Array.FindIndex(row, e => ReferenceEquals(e, focused)) + direction;

                if (at >= 0 && at < row.Length)
                    FocusElement(row[at]);

                return true;
            }

            if (ReferenceEquals(focused, _backButton))
                return true;

            if (IndexOf(LanguageColumn(), focused) >= 0)
            {
                if (direction > 0)
                    FocusFirstOption();

                return true;
            }

            if (_optionButtons.Any(b => ReferenceEquals(b, focused)))
            {
                if (direction < 0)
                {
                    Button language = _languageButtons.FirstOrDefault(
                        b => (string)b.Tag == "lang:" + _activeLanguageKey);

                    FocusElement(language != null ? (UIElement)language : _offButton);
                }

                return true;
            }

            FocusElement(FindInitialElement());
            return true;
        }

        private List<UIElement> LanguageColumn()
        {
            List<UIElement> column = new List<UIElement> { _offButton };
            column.AddRange(_languageButtons.Cast<UIElement>());
            return column;
        }

        private static int IndexOf(List<UIElement> list, IInputElement element)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], element))
                    return i;
            }

            return -1;
        }

        // Up past the first item goes to Back; down past the last goes to
        // the delay row. Otherwise one step, no wrap-around.
        private void MoveInColumn(List<UIElement> column, int index, int direction)
        {
            int next = index + direction;

            if (next < 0)
            {
                FocusElement(_backButton);
                return;
            }

            if (next >= column.Count)
            {
                FocusElement(_delayMinus);
                return;
            }

            FocusElement(column[next]);
        }

        private void FocusFirstOption()
        {
            if (_optionButtons.Count == 0)
                return;

            Button selected = _optionButtons.FirstOrDefault(b => ((SubtitleOption)b.Tag).IsSelected);
            FocusElement(selected ?? _optionButtons[0]);
        }

        private static void FocusElement(UIElement element)
        {
            if (element == null)
                return;

            element.Focus();

            IInputElement input = element as IInputElement;
            if (input != null)
                Keyboard.Focus(input);
        }

        // Identity of the focused language/option button, or null when
        // focus is elsewhere (so a refresh never steals focus).
        private string FocusedIdentity()
        {
            Button focused = Keyboard.FocusedElement as Button;

            if (focused == null)
                return null;

            string language = focused.Tag as string;
            if (language != null && _languageButtons.Contains(focused))
                return language;

            SubtitleOption option = focused.Tag as SubtitleOption;
            if (option != null && _optionButtons.Contains(focused))
                return "opt:" + option.Identity;

            return null;
        }

        private void RestoreFocus(string identity)
        {
            if (identity.StartsWith("opt:", StringComparison.Ordinal))
            {
                string wanted = identity.Substring(4);

                Button option = _optionButtons.FirstOrDefault(
                    b => ((SubtitleOption)b.Tag).Identity == wanted);

                if (option != null)
                {
                    FocusElement(option);
                    return;
                }

                // The option is gone (e.g. an addon result replaced by the
                // loaded track): stay in the options if any, else the language.
                if (_optionButtons.Count > 0)
                {
                    FocusElement(_optionButtons[0]);
                    return;
                }
            }
            else
            {
                Button language = _languageButtons.FirstOrDefault(b => (string)b.Tag == identity);

                if (language != null)
                {
                    FocusElement(language);
                    return;
                }
            }

            FocusElement(FindInitialElement());
        }

        // ---------------------------------------------------------------
        // SMALL HELPERS
        // ---------------------------------------------------------------

        private void Raise(EventHandler handler)
        {
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void RaiseDelay(double delta)
        {
            EventHandler<double> handler = DelayStepRequested;
            if (handler != null)
                handler(this, delta);
        }

        private static readonly Brush IdleBrush =
            new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        private static readonly Brush SelectedBrush =
            new SolidColorBrush(Color.FromArgb(0x99, 0x33, 0x88, 0xFF));

        private static object LanguageContent(string text, bool active)
        {
            return new TextBlock
            {
                Text = (active ? "● " : "○ ") + text,
                Foreground = Brushes.White,
                FontSize = 16,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private static ScrollViewer NewScroll(UIElement content)
        {
            ScrollViewer scroll = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                IsTabStop = false
            };

            // Mouse click-and-drag scrolling for the long language and
            // option lists (same helper as the rest of the app).
            DragScroll.Attach(scroll, DragScrollAxis.Vertical);

            return scroll;
        }

        private Button NewButton(string text, double width)
        {
            Button button = new Button
            {
                Style = ButtonStyle(),
                Background = IdleBrush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };

            if (text != null)
                button.Content = LanguageText(text);

            if (width > 0)
                button.Width = width;

            // Keyboard/controller movement scrolls the focused item into view.
            button.GotKeyboardFocus += (s, e) =>
            {
                FrameworkElement element = s as FrameworkElement;
                if (element != null) element.BringIntoView();
            };

            return button;
        }

        private static object LanguageText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 16
            };
        }

        // Built in code (no XAML parsing) so it cannot fail at load time.
        private static Style ButtonStyle()
        {
            if (_buttonStyle != null)
                return _buttonStyle;

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "Chrome");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.MarginProperty, new Thickness(4, 2, 4, 2));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.White, "Chrome"));
            focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
            template.Triggers.Add(focused);

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0xDD, 0x33, 0x33, 0x33)),
                "Chrome"));
            template.Triggers.Add(hover);

            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, true));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            _buttonStyle = style;
            return style;
        }
    }
}
