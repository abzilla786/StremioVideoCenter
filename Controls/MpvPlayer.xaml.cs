using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenterControls.Player;
using StremioVideoCenterControls.Player.Menus;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace StremioVideoCenterControls
{
    public partial class MpvPlayer : UserControl
    {
        private MpvHost _mpvHost;
        private PlayerSession _session;
        private PlayerFocusController _focus;

        private readonly PlayerUiState _uiState = new PlayerUiState();
        private readonly TrackSelectionMenu _trackMenu = new TrackSelectionMenu();
        private readonly SettingsMenu _settingsMenu = new SettingsMenu();
        private bool _pendingTrackMenuIsAudio;
        private bool _disposed;

        // Loaded and PlayVideoAsync can both request startup at the same time.
        // Serialize startup so PlayerSession.StartAsync cannot tear down a
        // process/IPC connection that another startup call is still using.
        private readonly SemaphoreSlim _startupGate = new SemaphoreSlim(1, 1);

        private DispatcherTimer _progressTimer;
        private DispatcherTimer _hideControlsTimer;

        private Window _fullscreenWindow;
        private WindowState _previousWindowState;
        private WindowStyle _previousWindowStyle;
        private ResizeMode _previousResizeMode;

        public event EventHandler CloseRequested;
        public event EventHandler FullscreenChanged;

        // Slice 16B (Persistent Profile-Owned Watch State): fires on
        // every tick of the EXISTING PlayerSession.StateChanged event -
        // the same one that already drives RefreshFromState's seek-bar/
        // time-text updates via the existing ProgressTimer_Tick ->
        // PollPositionAsync loop. No second timer or polling mechanism
        // is introduced. This also covers the end-of-file transition,
        // since PlayerSession already raises StateChanged (in addition
        // to its own separate EndOfFileReached event) when it sets
        // State.ReachedEndOfFile = true - see PlayerSession.
        // Ipc_EventReceived's "end-file" case. Consumers (e.g.
        // StremioVideoCenterView's Watch State integration) read
        // CurrentPlaybackState from this event's handler rather than
        // this event carrying the state itself, keeping this control's
        // public surface minimal.
        public event EventHandler PlaybackStateChanged;

        // Raised on the UI thread when mpv reports end-file reason=error
        // with a file_error (a genuine load/playback failure). Never raised
        // for eof/stop/quit.
        public event EventHandler PlaybackFailed;

        // Raised on the UI thread once mpv confirms the selected file
        // loaded (PlayerSession.PlaybackEstablished). Always delivered
        // before the PlaybackStateChanged ticks that follow it.
        public event EventHandler PlaybackEstablished;

        // Null before a session exists / after DisposePlayer. Exposes the
        // exact same PlaybackState instance RefreshFromState already
        // reads from - not a copy, not a second source of truth.
        public PlaybackState CurrentPlaybackState
        {
            get { return _session?.State; }
        }

        public MpvPlayer()
        {
            InitializeComponent();

            Loaded += MpvPlayer_Loaded;
            Unloaded += MpvPlayer_Unloaded;
            SizeChanged += MpvPlayer_SizeChanged;
            PreviewKeyDown += MpvPlayer_PreviewKeyDown;
            MouseMove += Player_MouseMove;
            MouseLeave += Player_MouseLeave;

            _trackMenu.TrackSelected += TrackMenu_TrackSelected;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _progressTimer.Tick += ProgressTimer_Tick;

            _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hideControlsTimer.Tick += HideControlsTimer_Tick;
        }

        // =====================================================================
        // STARTUP / LIFECYCLE
        // =====================================================================

        private async void MpvPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;

            if (_focus == null)
                BuildFocusGrid();

            WireSliders();

            // Open the controls chrome (Close button included) before attempting
            // to start mpv, not only after it succeeds. Previously ShowControls()
            // only ran on the success path, so a startup failure left the Popup
            // never opened - i.e. no Close button anywhere on screen to click.
            SyncControlsPopupSize();
            ShowControls();
            FocusDefaultInPopup();

            try
            {
                StartupTrace.Log("MpvPlayer_Loaded: about to call EnsureStartedAsync");
                await EnsureStartedAsync();
                _progressTimer.Start();
                RestartHideTimer();
            }
            catch (Exception ex)
            {
                // Details go to the log (URL-stripped); the dialog stays generic
                // so no command text or URL can reach the user's screen.
                StremioVideoCenterLogger.Info(
                    "MpvPlayer_Loaded: startup failed - " + UserFacingErrors.ForLog(ex));
                MessageBox.Show(
                    UserFacingErrors.ForPlayerStartup(ex),
                    "MPV Playback Error");
                // Controls (and Close) remain visible/open regardless.
            }
        }

        private void MpvPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            // The Video Center manages the player lifetime explicitly.
        }

        private void BuildFocusGrid()
        {
            FocusGroup topRow = new FocusGroup(CloseButton);
            FocusGroup centerRow = new FocusGroup(PlayPauseButton);
            FocusGroup bottomRow = new FocusGroup(
                BottomPlayPauseButton, Back10Button, Forward10Button,
                MuteButton, VolumeSlider, AudioButton, SubtitleButton,
                SettingsButton, FullscreenButton);

            FocusGrid grid = new FocusGrid { DefaultElement = PlayPauseButton };
            grid.AddRow(topRow);
            grid.AddRow(centerRow);
            grid.AddRow(bottomRow);

            _focus = new PlayerFocusController(grid);
        }

        private void WireSliders()
        {
            SeekSlider.StepAmount = 5.0;
            SeekSlider.OnStepLeft = amount => { var ignored = SeekRelativeAsync(-amount); };
            SeekSlider.OnStepRight = amount => { var ignored = SeekRelativeAsync(amount); };

            VolumeSlider.StepAmount = 5.0;
            VolumeSlider.OnStepLeft = amount => AdjustVolume(-amount);
            VolumeSlider.OnStepRight = amount => AdjustVolume(amount);
        }

        private async Task EnsureStartedAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvPlayer));

            StartupTrace.Log("EnsureStartedAsync: entered, waiting for gate");
            await _startupGate.WaitAsync();
            StartupTrace.Log("EnsureStartedAsync: gate acquired");
            try
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(MpvPlayer));

                if (_mpvHost == null)
                {
                    _mpvHost = new MpvHost();
                    VideoHost.Children.Add(_mpvHost);
                }

                if (_session == null)
                {
                    _session = new PlayerSession(_mpvHost);
                    _session.StateChanged += Session_StateChanged;
                    _session.TracksChanged += Session_StateChanged;
                    _session.EndOfFileReached += Session_EndOfFileReached;
                    _session.PlaybackFailed += Session_PlaybackFailed;
                    _session.PlaybackEstablished += Session_PlaybackEstablished;
                    _session.Disconnected += Session_Disconnected;
                }

                // IMPORTANT: Loaded and PlayVideoAsync can enter this method
                // concurrently. Only one caller may execute StartAsync.
                // PlayerSession.StartAsync begins by tearing down any existing
                // mpv process/IPC, so concurrent calls could otherwise destroy
                // the first startup while it is still connecting/initialising.
                if (_session.IsRunning)
                {
                    StartupTrace.Log("EnsureStartedAsync: session already running, skipping StartAsync");
                    return;
                }

                // Let the HwndHost finish creating its native window before mpv
                // tries to embed into it.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                int attemptId = StartupTrace.NewAttemptId();
                StartupTrace.Log("EnsureStartedAsync: attempt " + attemptId + " calling PlayerSession.StartAsync");

                try
                {
                    await _session.StartAsync(_mpvHost.WindowHandle);
                    StartupTrace.Log("EnsureStartedAsync: attempt " + attemptId + " StartAsync completed successfully");
                }
                catch (Exception ex)
                {
                    StartupTrace.Log("EnsureStartedAsync: attempt " + attemptId + " StartAsync threw: " + UserFacingErrors.ForLog(ex));
                    throw;
                }
            }
            finally
            {
                StartupTrace.Log("EnsureStartedAsync: releasing gate");
                _startupGate.Release();
            }
        }

        public async Task PlayVideoAsync(PlaybackContext context)
        {
            if (_disposed) return;

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // Keep the controls chrome (Close button included) visible
            // regardless of whether this start/play attempt succeeds - a
            // failure here should never leave the player with no way to exit.
            ShowControls();

            StartupTrace.Log("PlayVideoAsync: about to call EnsureStartedAsync");
            await EnsureStartedAsync();

            // EnsureStartedAsync has now fully completed (including a real
            // PlayerSession.StartAsync call if one was needed) - only past
            // this point does loadfile ever get issued.
            StartupTrace.Log("PlayVideoAsync: startup complete, issuing loadfile");
            await _session.PlayAsync(context);

            RestartHideTimer();
        }

        public void DisposePlayer()
        {
            if (_disposed) return;
            _disposed = true;

            try { _progressTimer?.Stop(); } catch { }
            try { _hideControlsTimer?.Stop(); } catch { }
            try { ControlsPopup.IsOpen = false; } catch { }
            try { PreviewKeyDown -= MpvPlayer_PreviewKeyDown; } catch { }
            try { SizeChanged -= MpvPlayer_SizeChanged; } catch { }
            try { Unloaded -= MpvPlayer_Unloaded; } catch { }
            try { Loaded -= MpvPlayer_Loaded; } catch { }
            try { _trackMenu.TrackSelected -= TrackMenu_TrackSelected; } catch { }
            try { _startupGate.Dispose(); } catch { }

            if (_session != null)
            {
                try
                {
                    _session.StateChanged -= Session_StateChanged;
                    _session.TracksChanged -= Session_StateChanged;
                    _session.EndOfFileReached -= Session_EndOfFileReached;
                    _session.PlaybackFailed -= Session_PlaybackFailed;
                    _session.PlaybackEstablished -= Session_PlaybackEstablished;
                    _session.Disconnected -= Session_Disconnected;
                    _session.Dispose();
                }
                catch { }
                _session = null;
            }

            try
            {
                if (_mpvHost != null)
                    VideoHost.Children.Remove(_mpvHost);
            }
            catch { }

            _mpvHost = null;
        }

        // =====================================================================
        // POPUP SIZING / PLACEMENT
        // =====================================================================

        private void MpvPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try { _mpvHost?.SynchronizeNativeWindow(); }
            catch { }

            SyncControlsPopupSize();
        }

        private void SyncControlsPopupSize()
        {
            if (ControlsPopup == null || RootGrid == null)
                return;

            double width = Math.Max(1.0, RootGrid.ActualWidth);
            double height = Math.Max(1.0, RootGrid.ActualHeight);

            ControlsPopup.Width = width;
            ControlsPopup.Height = height;
            ControlsOverlay.Width = width;
            ControlsOverlay.Height = height;
        }

        // WPF Popup is a separate native window, which lets the controls render
        // above the embedded MPV HWND. Custom placement pins that window to the
        // exact top-left corner of the Video Center player surface instead of
        // allowing WPF to reposition it like a normal dropdown/context popup.
        private CustomPopupPlacement[] ControlsPopupPlacement(Size popupSize, Size targetSize, Point offset)
        {
            return new[] { new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None) };
        }

        // =====================================================================
        // PLAYBACK STATE -> UI
        // =====================================================================

        private void Session_StateChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshFromState));

            EventHandler handler = PlaybackStateChanged;
            if (handler != null)
            {
                Dispatcher.BeginInvoke(new Action(() => handler(this, EventArgs.Empty)));
            }
        }

        private void RefreshFromState()
        {
            if (_disposed || _session == null) return;

            PlaybackState state = _session.State;

            if (state.DurationSeconds > 0)
                SeekSlider.Maximum = state.DurationSeconds;

            if (!_uiState.IsSeekBarBeingDragged)
                SeekSlider.Value = Math.Max(0, Math.Min(SeekSlider.Maximum, state.PositionSeconds));

            string playGlyph = state.IsPlaying ? "\u275A\u275A" : "\u25B6";
            PlayPauseButton.Content = playGlyph;
            BottomPlayPauseButton.Content = playGlyph;

            if (!VolumeSlider.IsKeyboardFocusWithin)
                VolumeSlider.Value = Math.Max(0, Math.Min(100, state.Volume));

            MuteButton.Content = state.IsMuted ? "\U0001F507" : "\U0001F50A";

            TimeText.Text = FormatTime(state.PositionSeconds) + " / " + FormatTime(state.DurationSeconds);
        }

        private void Session_PlaybackEstablished(object sender, EventArgs e)
        {
            // Raised from the IPC read thread - marshal to the UI thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;

                EventHandler handler = PlaybackEstablished;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }));
        }

        private void Session_PlaybackFailed(object sender, EventArgs e)
        {
            // Raised from the IPC read thread - marshal to the UI thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;

                EventHandler handler = PlaybackFailed;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }));
        }

        private void Session_EndOfFileReached(object sender, EventArgs e)
        {
            // Keep-open/keep-open-pause already leaves the last frame visible and
            // pauses mpv itself, which flips IsPlaying via the "pause" property
            // event. Here we just make sure the controls stay visible so the
            // user can rewind/replay instead of auto-hiding onto a dead-looking
            // frozen frame.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowControls();
                _hideControlsTimer.Stop();
            }));
        }

        private void Session_Disconnected(object sender, EventArgs e)
        {
            // mpv exited unexpectedly. PlayVideoAsync's internal recovery path
            // rebuilds the process on the next play call; nothing else to do here.
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                seconds = 0;

            TimeSpan time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1)
                return time.ToString(@"hh\:mm\:ss");
            return time.ToString(@"mm\:ss");
        }

        private async void ProgressTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed || _session == null || !_session.IsRunning || _uiState.IsSeekBarBeingDragged)
                return;

            await _session.PollPositionAsync();
        }

        // =====================================================================
        // TRANSPORT COMMANDS
        // =====================================================================

        public async Task SeekRelativeAsync(double seconds)
        {
            if (_session == null) return;
            await _session.SeekRelativeAsync(seconds);
            ShowControls();
            RestartHideTimer();
        }

        public async Task SeekAbsoluteAsync(double seconds)
        {
            if (_session == null) return;
            await _session.SeekAbsoluteAsync(seconds);
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try { if (_session != null) await _session.TogglePauseAsync(); }
            catch { }
            ShowControls();
            RestartHideTimer();
        }

        private async void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            try { await SeekRelativeAsync(-10); }
            catch { }
        }

        private async void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            try { await SeekRelativeAsync(10); }
            catch { }
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try { if (_session != null) await _session.ToggleMuteAsync(); }
            catch { }
            ShowControls();
            RestartHideTimer();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseCloseRequested();
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _uiState.IsSeekBarBeingDragged = true;
            ShowControls();
            RestartHideTimer();
        }

        private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _uiState.IsSeekBarBeingDragged = false;
            try { if (_session != null) await _session.SeekAbsoluteAsync(SeekSlider.Value); }
            catch { }
            RestartHideTimer();
        }

        private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_disposed || _session == null || !_session.IsRunning) return;

            // Avoid sending a volume command for programmatic updates driven by
            // RefreshFromState (i.e. mpv telling us its own current volume).
            if (!VolumeSlider.IsKeyboardFocusWithin && !IsMouseOver) return;

            try { await _session.SetVolumeAsync(VolumeSlider.Value); }
            catch { }
            ShowControls();
            RestartHideTimer();
        }

        private void AdjustVolume(double delta)
        {
            VolumeSlider.Focus();
            VolumeSlider.Value = Math.Max(0, Math.Min(100, VolumeSlider.Value + delta));
            ShowControls();
            RestartHideTimer();
        }

        // =====================================================================
        // AUDIO / SUBTITLE / SETTINGS MENUS
        // =====================================================================

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            OpenTrackMenu(isAudio: true);
        }

        private void SubtitleButton_Click(object sender, RoutedEventArgs e)
        {
            OpenTrackMenu(isAudio: false);
        }

        private void OpenTrackMenu(bool isAudio)
        {
            if (_session == null || _focus == null) return;

            var tracks = isAudio ? _session.State.AudioTracks : _session.State.SubtitleTracks;
            int? selected = isAudio ? _session.State.SelectedAudioTrackId : _session.State.SelectedSubtitleTrackId;
            string title = isAudio ? "Audio" : "Subtitles";
            UIElement originButton = isAudio ? (UIElement)AudioButton : SubtitleButton;

            _pendingTrackMenuIsAudio = isAudio;

            FocusGroup group = _trackMenu.Populate(title, tracks, selected, includeOffOption: !isAudio);

            MenuHost.Content = _trackMenu;
            MenuHost.Visibility = Visibility.Visible;
            _uiState.OpenMenu = isAudio ? PlayerMenu.Audio : PlayerMenu.Subtitles;

            _focus.PushMenuScope(group, originButton);
            ShowControls();
            RestartHideTimer();
        }

        private async void TrackMenu_TrackSelected(object sender, TrackMenuItemSelectedEventArgs e)
        {
            try
            {
                if (_session != null)
                {
                    if (_pendingTrackMenuIsAudio)
                    {
                        if (e.TrackId.HasValue)
                            await _session.SelectAudioTrackAsync(e.TrackId.Value);
                    }
                    else
                    {
                        await _session.SelectSubtitleTrackAsync(e.TrackId);
                    }
                }
            }
            catch { }

            CloseActiveMenuAndRestoreFocus();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_focus == null) return;

            FocusGroup group = _settingsMenu.BuildFocusGroup();

            MenuHost.Content = _settingsMenu;
            MenuHost.Visibility = Visibility.Visible;
            _uiState.OpenMenu = PlayerMenu.Settings;

            _focus.PushMenuScope(group, SettingsButton);
            ShowControls();
            RestartHideTimer();
        }

        // Pops the focus scope (which also restores focus to the button that
        // opened the menu) and hides the menu surface.
        private void CloseActiveMenuAndRestoreFocus()
        {
            if (_focus != null)
                _focus.PopScope();

            MenuHost.Visibility = Visibility.Collapsed;
            MenuHost.Content = null;
            _uiState.OpenMenu = PlayerMenu.None;

            ShowControls();
            RestartHideTimer();
        }

        // =====================================================================
        // KEYBOARD / CONTROLLER INPUT
        //
        // Both the keyboard path (Overlay_PreviewKeyDown / MpvPlayer_PreviewKeyDown)
        // and the external controller path (HandleControllerInput, called by
        // whatever gamepad plumbing already exists in the Video Center) route
        // through the same Handle* methods below, so B/A/D-pad behaviour is
        // defined exactly once regardless of input source.
        // =====================================================================

        // Popup content is a separate PresentationSource, so this is the handler
        // that actually fires while focus is on a control inside the popup.
        private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandlePlayerKeyDown(e);
        }

        // Defensive fallback for the (normally brief) window where focus is on
        // the UserControl itself, before the popup has opened.
        private void MpvPlayer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandlePlayerKeyDown(e);
        }

        private void HandlePlayerKeyDown(KeyEventArgs e)
        {
            if (_disposed || _focus == null) return;

            switch (e.Key)
            {
                case Key.B:
                case Key.Escape:
                case Key.Back:
                    HandleClose();
                    e.Handled = true;
                    break;

                case Key.Enter:
                case Key.Space:
                    HandleActivate();
                    e.Handled = true;
                    break;

                case Key.Left:
                    _focus.MoveHorizontal(-1);
                    ShowControls();
                    RestartHideTimer();
                    e.Handled = true;
                    break;

                case Key.Right:
                    _focus.MoveHorizontal(1);
                    ShowControls();
                    RestartHideTimer();
                    e.Handled = true;
                    break;

                case Key.Up:
                    _focus.MoveVertical(-1);
                    ShowControls();
                    RestartHideTimer();
                    e.Handled = true;
                    break;

                case Key.Down:
                    _focus.MoveVertical(1);
                    ShowControls();
                    RestartHideTimer();
                    e.Handled = true;
                    break;
            }
        }

        // Called by the Video Center's existing gamepad plumbing.
        public bool HandleControllerInput(ControllerInput button)
        {
            if (_disposed || _focus == null) return false;

            ShowControls();

            switch (button)
            {
                case ControllerInput.B:
                    HandleClose();
                    return true;

                case ControllerInput.A:
                    HandleActivate();
                    return true;

                case ControllerInput.DPadLeft:
                    _focus.MoveHorizontal(-1);
                    RestartHideTimer();
                    return true;

                case ControllerInput.DPadRight:
                    _focus.MoveHorizontal(1);
                    RestartHideTimer();
                    return true;

                case ControllerInput.DPadUp:
                    _focus.MoveVertical(-1);
                    RestartHideTimer();
                    return true;

                case ControllerInput.DPadDown:
                    _focus.MoveVertical(1);
                    RestartHideTimer();
                    return true;
            }

            return false;
        }

        // B is context-sensitive:
        //   - Focus on a slider (Seek/Volume) at the root scope: exit the
        //     slider back to the default control, don't close the player.
        //   - A menu is open (Audio/Subtitle/Settings): pop it, restore focus
        //     to the button that opened it, don't close the player.
        //   - Otherwise (root scope, non-slider focus): close the player.
        private void HandleClose()
        {
            UIElement focused = Keyboard.FocusedElement as UIElement;

            if (focused is PlayerSlider && _focus.IsAtRootScope)
            {
                _focus.FocusRootDefault();
                ShowControls();
                RestartHideTimer();
                return;
            }

            if (!_focus.IsAtRootScope)
            {
                CloseActiveMenuAndRestoreFocus();
                return;
            }

            RaiseCloseRequested();
        }

        private void HandleActivate()
        {
            UIElement focused = Keyboard.FocusedElement as UIElement;

            if (focused == null)
            {
                _focus.FocusRootDefault();
                return;
            }

            Button button = focused as Button;
            if (button != null)
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // A on a slider intentionally does nothing beyond keeping focus
            // there - this avoids an accidental play/pause while adjusting
            // seek or volume.
            ShowControls();
            RestartHideTimer();
        }

        private void FocusDefaultInPopup()
        {
            if (_focus == null) return;

            ShowControls();

            // Popup is a separate native window; queue focus until it is open
            // and laid out so the button actually receives keyboard focus
            // instead of the parent Video Center retaining it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ControlsPopup.IsOpen = true;
                    _focus.FocusRootDefault();
                }
                catch { }
            }), DispatcherPriority.Input);
        }

        // =====================================================================
        // MOUSE / AUTO-HIDE
        // =====================================================================

        private void Player_MouseMove(object sender, MouseEventArgs e)
        {
            ShowControls();
            RestartHideTimer();
        }

        private void Player_MouseLeave(object sender, MouseEventArgs e)
        {
            RestartHideTimer();
        }

        private void ShowControls()
        {
            SyncControlsPopupSize();
            ControlsOverlay.Visibility = Visibility.Visible;
            ControlsPopup.IsOpen = true;
            ControlsOverlay.UpdateLayout();
            _uiState.ControlsVisible = true;
        }

        private void HideControlsTimer_Tick(object sender, EventArgs e)
        {
            if (_uiState.OpenMenu != PlayerMenu.None) return;

            if (Keyboard.FocusedElement is Slider || Keyboard.FocusedElement is Button)
                return;

            if (_session != null && _session.State.IsPlaying)
            {
                ControlsOverlay.Visibility = Visibility.Collapsed;
                ControlsPopup.IsOpen = false;
                _uiState.ControlsVisible = false;
            }
        }

        private void RestartHideTimer()
        {
            if (_hideControlsTimer == null) return;

            _hideControlsTimer.Stop();
            _hideControlsTimer.Start();
        }

        // =====================================================================
        // FULLSCREEN
        // =====================================================================

        private void ToggleFullscreen()
        {
            _fullscreenWindow = Window.GetWindow(this);
            if (_fullscreenWindow == null) return;

            if (!_uiState.IsFullscreen)
            {
                _previousWindowState = _fullscreenWindow.WindowState;
                _previousWindowStyle = _fullscreenWindow.WindowStyle;
                _previousResizeMode = _fullscreenWindow.ResizeMode;

                _fullscreenWindow.WindowStyle = WindowStyle.None;
                _fullscreenWindow.ResizeMode = ResizeMode.NoResize;
                _fullscreenWindow.WindowState = WindowState.Maximized;
                _uiState.IsFullscreen = true;
            }
            else
            {
                _fullscreenWindow.WindowStyle = _previousWindowStyle;
                _fullscreenWindow.ResizeMode = _previousResizeMode;
                _fullscreenWindow.WindowState = _previousWindowState;
                _uiState.IsFullscreen = false;
            }

            FullscreenChanged?.Invoke(this, EventArgs.Empty);
            ShowControls();
            RestartHideTimer();
        }

        private void RaiseCloseRequested()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
