using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenterControls.Player;
using StremioVideoCenterControls.Player.Menus;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        // Drives the transient overlay (activity polling + fade decisions).
        // Named _hideControlsTimer historically; it now ticks continuously
        // while the player is alive instead of being re-armed per interaction.
        private DispatcherTimer _hideControlsTimer;

        private readonly OverlayVisibilityPolicy _overlayPolicy =
            new OverlayVisibilityPolicy(DateTime.UtcNow);
        private bool _overlayVisible = true;

        // Last cursor position seen by the activity poll. The mpv video is a
        // native HwndHost, which swallows WPF mouse events over the video, so
        // mouse movement is also detected by sampling the OS cursor.
        private bool _haveCursorSample;
        private int _lastCursorX;
        private int _lastCursorY;

        // True while RefreshFromState moves the volume slider to mpv's value,
        // so that programmatic change is not sent back to mpv as a command.
        private bool _applyingVolumeFromState;

        private const double SeekStepSeconds = 10;
        private const double VolumeStep = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

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

        // Episode navigation requests (Previous/Next button, PageUp/PageDown).
        // The player only asks; the Video Center owns episode resolution.
        public event EventHandler PreviousEpisodeRequested;
        public event EventHandler NextEpisodeRequested;

        // Raised on the UI thread for a genuine end of file only (mpv
        // reason=eof / eof-reached on an established file) - never for
        // error, stop, quit or manual exit. Consumers decide what follows
        // (completion, auto-next).
        public event EventHandler EndOfFileReached;

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

        // The PlaybackContext the session is currently loading/playing (the
        // one most recently handed to PlayVideoAsync).
        public PlaybackContext CurrentContext
        {
            get { return _session?.Context; }
        }

        public MpvPlayer()
        {
            InitializeComponent();

            Loaded += MpvPlayer_Loaded;
            Unloaded += MpvPlayer_Unloaded;
            SizeChanged += MpvPlayer_SizeChanged;
            PreviewKeyDown += MpvPlayer_PreviewKeyDown;
            MouseMove += Player_MouseMove;

            _trackMenu.TrackSelected += TrackMenu_TrackSelected;
            _trackMenu.SubtitleDelayStepRequested += TrackMenu_SubtitleDelayStepRequested;
            _trackMenu.SubtitleDelayResetRequested += TrackMenu_SubtitleDelayResetRequested;

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _progressTimer.Tick += ProgressTimer_Tick;

            _hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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

            // The overlay timer runs for the whole life of the player (also
            // when startup fails, so Exit stays reachable).
            _hideControlsTimer.Start();

            // The controls popup is a separate native window: it does not move
            // when the host window moves/changes state, so follow those events.
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.LocationChanged += HostWindow_GeometryChanged;
                _hostWindow.StateChanged += HostWindow_GeometryChanged;
            }

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
            // Rows, top to bottom: Exit / seek bar / bottom control bar. The
            // centre play-state indicator is deliberately NOT part of the
            // focus model - it is not focusable at all.
            FocusGroup topRow = new FocusGroup(CloseButton);
            FocusGroup seekRow = new FocusGroup(SeekSlider);
            FocusGroup bottomRow = new FocusGroup(
                BottomPlayPauseButton, Back10Button, Forward10Button,
                PreviousEpisodeButton, NextEpisodeButton,
                MuteButton, VolumeSlider, AudioButton, SubtitleButton,
                SettingsButton, FullscreenButton);

            // The bottom Play/Pause button is the first actionable control and
            // the default focus target (initial focus, and after a menu closes
            // without a recorded origin).
            FocusGrid grid = new FocusGrid { DefaultElement = BottomPlayPauseButton };
            grid.AddRow(topRow);
            grid.AddRow(seekRow);
            grid.AddRow(bottomRow);

            _focus = new PlayerFocusController(grid);
        }

        private void WireSliders()
        {
            // Seek bar: alone in its row, always consumes Left/Right.
            SeekSlider.StepAmount = 5.0;
            SeekSlider.RequiresEngagement = false;
            SeekSlider.OnStepLeft = amount => { var ignored = SeekRelativeAsync(-amount); };
            SeekSlider.OnStepRight = amount => { var ignored = SeekRelativeAsync(amount); };

            // Volume slider sits in the middle of the button row: it only
            // consumes Left/Right once engaged with A/Enter, so it can never
            // trap focus.
            VolumeSlider.StepAmount = VolumeStep;
            VolumeSlider.RequiresEngagement = true;
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

            // The player may have been closed while startup was awaited (user
            // pressed Exit): do nothing rather than fail on a disposed session.
            PlayerSession session = _session;
            if (_disposed || session == null) return;

            await session.PlayAsync(context);

            RestartHideTimer();
        }

        public void DisposePlayer()
        {
            if (_disposed) return;
            _disposed = true;

            try { _progressTimer?.Stop(); } catch { }
            try { _hideControlsTimer?.Stop(); } catch { }
            try { _noticeTimer?.Stop(); } catch { }

            // A menu left open over a closing player resumes nothing.
            _menuGuard.Discard();

            if (_hostWindow != null)
            {
                _hostWindow.LocationChanged -= HostWindow_GeometryChanged;
                _hostWindow.StateChanged -= HostWindow_GeometryChanged;
                _hostWindow = null;
            }

            // Closing the player must not leave the host window borderless/
            // maximised because fullscreen was toggled on.
            try { if (_uiState.IsFullscreen) ToggleFullscreen(); } catch { }

            try { ControlsPopup.IsOpen = false; } catch { }
            try { PreviewKeyDown -= MpvPlayer_PreviewKeyDown; } catch { }
            try { SizeChanged -= MpvPlayer_SizeChanged; } catch { }
            try { Unloaded -= MpvPlayer_Unloaded; } catch { }
            try { Loaded -= MpvPlayer_Loaded; } catch { }
            try { _trackMenu.TrackSelected -= TrackMenu_TrackSelected; } catch { }
            try { _trackMenu.SubtitleDelayStepRequested -= TrackMenu_SubtitleDelayStepRequested; } catch { }
            try { _trackMenu.SubtitleDelayResetRequested -= TrackMenu_SubtitleDelayResetRequested; } catch { }
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

        private Window _hostWindow;
        private bool _popupNudge;

        // Re-places the (separate-window) popup after the host window moved or
        // changed state: size/offset are recomputed from the CURRENT geometry,
        // and a sub-pixel offset change forces WPF to reposition the popup.
        private void HostWindow_GeometryChanged(object sender, EventArgs e)
        {
            if (_disposed) return;

            SyncControlsPopupSize();

            if (ControlsPopup.IsOpen)
            {
                _popupNudge = !_popupNudge;
                ControlsPopup.HorizontalOffset += _popupNudge ? 0.01 : -0.01;
            }
        }

        // A DPI change (moving to a monitor with another scale) changes the
        // device-pixel size of the player without changing its DIP size.
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);

            if (_disposed) return;

            try { _mpvHost?.SynchronizeNativeWindow(); }
            catch { }

            SyncControlsPopupSize();
        }

        private void MpvPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try { _mpvHost?.SynchronizeNativeWindow(); }
            catch { }

            SyncControlsPopupSize();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

        private const uint MonitorDefaultToNearest = 2;

        private static PixelRect ToPixelRect(NativeRect rect)
        {
            return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        // The part of the player surface that is really on screen, in device
        // pixels (see VisibleBounds). Null when it cannot be determined yet
        // (not attached to a presentation source / monitor lookup failed), in
        // which case the nominal container size is used.
        private PixelRect? GetVisiblePlayerBounds(out PixelRect nominal)
        {
            nominal = default(PixelRect);

            try
            {
                Point topLeft = RootGrid.PointToScreen(new Point(0, 0));
                Point bottomRight = RootGrid.PointToScreen(new Point(RootGrid.ActualWidth, RootGrid.ActualHeight));
                nominal = new PixelRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);

                // The host window's client area (not its outer bounds, which
                // include title bar/borders that are not part of the surface).
                // The Video Center window is laid over Playnite's window (its
                // owner), so the owner's client area is the viewport the user is
                // meant to see: clip to both.
                Window window = Window.GetWindow(this);
                PixelRect? host = ClientAreaOf(window);
                PixelRect? owner = ClientAreaOf(window != null ? window.Owner : null);

                if (host.HasValue && owner.HasValue)
                    host = PixelRect.Intersect(host.Value, owner.Value);
                else if (owner.HasValue)
                    host = owner;

                NativePoint centre = new NativePoint
                {
                    X = (int)((nominal.Left + nominal.Right) / 2),
                    Y = (int)((nominal.Top + nominal.Bottom) / 2)
                };

                IntPtr monitor = MonitorFromPoint(centre, MonitorDefaultToNearest);
                NativeMonitorInfo info = new NativeMonitorInfo { Size = Marshal.SizeOf(typeof(NativeMonitorInfo)) };

                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                    return null;

                return VisibleBounds.Compute(nominal, host, ToPixelRect(info.Monitor), ToPixelRect(info.Work));
            }
            catch
            {
                return null;
            }
        }

        private static PixelRect? ClientAreaOf(Window window)
        {
            FrameworkElement content = window != null ? window.Content as FrameworkElement : null;

            if (content == null || !content.IsVisible || content.ActualWidth <= 0 || content.ActualHeight <= 0)
                return null;

            Point topLeft = content.PointToScreen(new Point(0, 0));
            Point bottomRight = content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight));
            return new PixelRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }

        // Sizes (and positions) the controls overlay to the VISIBLE part of the
        // player surface, so the bottom control bar - anchored to the overlay's
        // bottom edge - is always inside what the user can see, whatever the
        // container's nominal size, and adapts on resize/fullscreen.
        private void SyncControlsPopupSize()
        {
            if (ControlsPopup == null || RootGrid == null)
                return;

            double width = Math.Max(1.0, RootGrid.ActualWidth);
            double height = Math.Max(1.0, RootGrid.ActualHeight);
            double offsetX = 0;
            double offsetY = 0;

            PixelRect nominal;
            PixelRect? visible = GetVisiblePlayerBounds(out nominal);
            PresentationSource source = PresentationSource.FromVisual(RootGrid);

            if (visible.HasValue && source != null && source.CompositionTarget != null && !nominal.IsEmpty)
            {
                // Device pixels -> device-independent units.
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                PixelRect v = visible.Value;

                width = Math.Max(1.0, v.Width * fromDevice.M11);
                height = Math.Max(1.0, v.Height * fromDevice.M22);
                offsetX = (v.Left - nominal.Left) * fromDevice.M11;
                offsetY = (v.Top - nominal.Top) * fromDevice.M22;
            }

            if (Math.Abs(ControlsPopup.Width - width) < 0.5 &&
                Math.Abs(ControlsPopup.Height - height) < 0.5 &&
                Math.Abs(ControlsPopup.HorizontalOffset - offsetX) < 0.5 &&
                Math.Abs(ControlsPopup.VerticalOffset - offsetY) < 0.5)
            {
                return;
            }

            ControlsPopup.HorizontalOffset = offsetX;
            ControlsPopup.VerticalOffset = offsetY;
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
            // 'offset' is the popup's Horizontal/VerticalOffset, which
            // SyncControlsPopupSize sets to the visible part's position inside
            // the (possibly larger) nominal player container.
            return new[] { new CustomPopupPlacement(offset, PopupPrimaryAxis.None) };
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
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed)
                        handler(this, EventArgs.Empty);
                }));
            }
        }

        private void RefreshFromState()
        {
            if (_disposed || _session == null) return;

            PlaybackState state = _session.State;

            if (state.DurationSeconds > 0)
                SeekSlider.Maximum = state.DurationSeconds;

            if (!_uiState.IsSeekBarBeingDragged)
            {
                SeekSlider.Value = Math.Max(0, Math.Min(SeekSlider.Maximum, state.PositionSeconds));
                CurrentTimeText.Text = FormatTime(state.PositionSeconds);
            }

            DurationText.Text = FormatTime(state.DurationSeconds);

            ApplyPlayState(state.IsPlaybackEstablished, state.IsPlaying);

            // mpv's volume always drives the slider, except while the mouse is
            // actually dragging it (a keyboard-focused slider must still follow
            // mpv, or keyboard/controller volume steps would not show).
            if (!VolumeSlider.IsMouseCaptureWithin)
            {
                _applyingVolumeFromState = true;
                try { VolumeSlider.Value = Math.Max(0, Math.Min(100, state.Volume)); }
                finally { _applyingVolumeFromState = false; }
            }

            MuteButton.Content = state.IsMuted ? "\U0001F507" : "\U0001F50A";

            // Keep an open Audio/Subtitles menu in step with mpv: rebuild it if
            // the track list/selection changed, otherwise just the delay value.
            RefreshOpenTrackMenuIfStale();
            EnforceMenuPlaybackHold(state);

            if (_uiState.OpenMenu == PlayerMenu.Subtitles)
                _trackMenu.UpdateSubtitleDelay(state.SubtitleDelaySeconds);
        }

        // The ONE place play/pause visuals are derived from playback state,
        // so the real bottom button and the centre indicator can never
        // disagree. The bottom button shows the action it will perform; the
        // centre indicator (display only) shows the current state.
        private void ApplyPlayState(bool isEstablished, bool isPlaying)
        {
            BottomPlayPauseButton.Content = PlayerStateRules.ActionGlyph(isPlaying);
            CenterPlayGlyph.Text = PlayerStateRules.CentreGlyph(isEstablished, isPlaying);
        }

        private void Session_PlaybackEstablished(object sender, EventArgs e)
        {
            // Raised from the IPC read thread - marshal to the UI thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;

                // Media launched: initial keyboard/controller focus goes to
                // the bottom Play/Pause button (never the centre indicator).
                // (When another episode is loaded into this same player the user
                // is already inside the controls - e.g. on Next - so focus is
                // left where it is.)
                if (_focus != null && _focus.IsAtRootScope && !ControlsOverlay.IsKeyboardFocusWithin)
                {
                    ShowControls();
                    _focus.FocusRootDefault();
                }

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
                if (_disposed) return;
                ShowControls();

                EventHandler handler = EndOfFileReached;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }));
        }

        // =====================================================================
        // EPISODE NAVIGATION (TV / anime only)
        // =====================================================================

        // Shows the series/episode line and Previous/Next for an episode, or
        // hides all of it for a movie (null). A direction with no episode is
        // disabled, which also removes it from focus movement.
        public void SetEpisodeNavigation(EpisodeContext episode)
        {
            if (_disposed) return;

            if (episode == null)
            {
                EpisodeInfoPanel.Visibility = Visibility.Collapsed;
                PreviousEpisodeButton.Visibility = Visibility.Collapsed;
                NextEpisodeButton.Visibility = Visibility.Collapsed;
                return;
            }

            EpisodeSeriesText.Text = episode.SeriesTitle ?? string.Empty;
            EpisodeLabelText.Text = EpisodeContext.FormatLabel(episode.Current);
            EpisodeInfoPanel.Visibility = Visibility.Visible;

            PreviousEpisodeButton.Visibility = Visibility.Visible;
            NextEpisodeButton.Visibility = Visibility.Visible;

            PreviousEpisodeButton.IsEnabled = episode.HasPrevious;
            NextEpisodeButton.IsEnabled = episode.HasNext;

            PreviousEpisodeButton.ToolTip = episode.HasPrevious
                ? "Previous episode \u2014 " + EpisodeContext.FormatCode(episode.Previous)
                : "No previous episode";
            NextEpisodeButton.ToolTip = episode.HasNext
                ? "Next episode \u2014 " + EpisodeContext.FormatCode(episode.Next)
                : "No next episode";

            // If focus was on a button that has just become disabled, hand it
            // back to the default control so it is never stranded.
            if (_focus != null &&
                (ReferenceEquals(Keyboard.FocusedElement, PreviousEpisodeButton) && !episode.HasPrevious ||
                 ReferenceEquals(Keyboard.FocusedElement, NextEpisodeButton) && !episode.HasNext))
            {
                _focus.FocusRootDefault();
            }
        }

        // A short, transient one-line notice (e.g. "Next episode - S03E05 -
        // Title") that rides on the overlay and disappears by itself.
        public void ShowNotice(string text)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text)) return;

            NoticeText.Text = text;
            NoticeBorder.Visibility = Visibility.Visible;
            ShowControls();

            if (_noticeTimer == null)
            {
                _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _noticeTimer.Tick += (s, e) =>
                {
                    _noticeTimer.Stop();
                    NoticeBorder.Visibility = Visibility.Collapsed;
                };
            }

            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private DispatcherTimer _noticeTimer;

        private void PreviousEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseEpisodeRequest(PreviousEpisodeRequested);
        }

        private void NextEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseEpisodeRequest(NextEpisodeRequested);
        }

        private void RaiseEpisodeRequest(EventHandler handler)
        {
            ShowControls();

            if (handler != null)
                handler(this, EventArgs.Empty);
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

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        // The single Play/Pause command path (bottom button, Space, controller
        // X). mpv owns the state; the UI updates from the observed property.
        private async void TogglePlayPause()
        {
            try { if (_session != null) await _session.TogglePauseAsync(); }
            catch { }
            ShowControls();
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

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMute();
        }

        private async void ToggleMute()
        {
            try { if (_session != null) await _session.ToggleMuteAsync(); }
            catch { }
            ShowControls();
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

        private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            var ignored = CommitSeekAsync();
        }

        // Losing mouse capture without a mouse-up (focus stolen, window
        // deactivated) must still end the drag: otherwise the overlay would
        // stay pinned and position polling would stay paused forever.
        private void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e)
        {
            // Capture also moves from the slider to its thumb at the start of a
            // drag; only treat it as the end of the drag once the button is up.
            if (_uiState.IsSeekBarBeingDragged && Mouse.LeftButton != MouseButtonState.Pressed)
            {
                var ignored = CommitSeekAsync();
            }
        }

        private bool _seekCommitInFlight;

        // Sends the dragged-to position once. Stays in "seeking" (controls
        // visible, polling paused) until the command has been sent, so the
        // thumb does not snap back to the pre-seek position in between.
        private async Task CommitSeekAsync()
        {
            if (!_uiState.IsSeekBarBeingDragged || _seekCommitInFlight)
                return;

            _seekCommitInFlight = true;
            double target = SeekSlider.Value;

            try { if (_session != null) await _session.SeekAbsoluteAsync(target); }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Seek failed: " + UserFacingErrors.ForLog(ex));
            }
            finally
            {
                _seekCommitInFlight = false;
                _uiState.IsSeekBarBeingDragged = false;
            }

            if (!_disposed)
                ShowControls();
        }

        // Live time read-out while dragging the thumb.
        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_uiState.IsSeekBarBeingDragged)
                CurrentTimeText.Text = FormatTime(SeekSlider.Value);
        }

        private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Ignore the programmatic update that mirrors mpv's own volume.
            if (_applyingVolumeFromState) return;
            if (_disposed || _session == null || !_session.IsRunning) return;

            try { await _session.SetVolumeAsync(VolumeSlider.Value); }
            catch { }
            ShowControls();
        }

        // Keyboard/controller volume step. Sends the command to mpv directly
        // (mpv is the authority); the slider follows via the observed volume
        // property, so there is no second copy of the volume to keep in sync.
        private async void AdjustVolume(double delta)
        {
            if (_disposed || _session == null) return;

            double target = Math.Max(0, Math.Min(100, _session.State.Volume + delta));
            try { await _session.SetVolumeAsync(target); }
            catch { }
            ShowControls();
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

        private int _trackMenuVersion;

        // Builds the menu group from mpv's CURRENT state (what PlaybackState
        // mirrors): the loaded file's tracks, the selected one and the
        // subtitle delay. Used both to open the menu and to refresh it live.
        private FocusGroup BuildTrackMenuGroup(bool isAudio)
        {
            PlaybackState state = _session.State;

            _trackMenuVersion = state.TracksVersion;

            // Fit the (scrolling) track list inside the player: leave room for
            // the title, padding, and - for Subtitles - the delay controls.
            _trackMenu.SetMaxListHeight(ControlsOverlay.ActualHeight - (isAudio ? 150 : 330));

            return _trackMenu.Populate(
                isAudio ? "Audio" : "Subtitles",
                TrackMenuLayout.Build(
                    isSubtitles: !isAudio,
                    tracks: isAudio ? state.AudioTracks : state.SubtitleTracks,
                    selectedTrackId: isAudio ? state.SelectedAudioTrackId : state.SelectedSubtitleTrackId,
                    tracksLoaded: state.TracksLoaded,
                    delaySeconds: state.SubtitleDelaySeconds));
        }

        private void OpenTrackMenu(bool isAudio)
        {
            if (_session == null || _focus == null) return;

            // Menus never stack: opening one while another is open replaces it
            // and still returns focus to the right button on close. Playback is
            // NOT resumed in between - the state captured when the FIRST menu
            // opened still applies.
            if (!_focus.IsAtRootScope)
                CloseActiveMenuAndRestoreFocus(restorePlayback: false);

            UIElement originButton = isAudio ? (UIElement)AudioButton : SubtitleButton;

            // Capture mpv's state BEFORE the menu exists, then pause first.
            // Already paused (or still loading) -> nothing is paused and, on
            // close, nothing is resumed.
            PlaybackState state = _session.State;
            if (_menuGuard.Open(
                    state.IsPlaybackEstablished && state.IsPlaying,
                    _session.Context,
                    _session.PlayGeneration))
            {
                var pauseTask = HoldPlaybackForMenuAsync();
            }

            _pendingTrackMenuIsAudio = isAudio;

            FocusGroup group = BuildTrackMenuGroup(isAudio);
            _menuSerial++;

            MenuHost.Content = _trackMenu;
            MenuHost.Visibility = Visibility.Visible;
            _uiState.OpenMenu = isAudio ? PlayerMenu.Audio : PlayerMenu.Subtitles;

            _focus.PushMenuScope(group, originButton);
            ShowControls();
            RestartHideTimer();
        }

        // =====================================================================
        // PLAYBACK WHILE A TRACK MENU IS OPEN
        //
        // Opening Audio/Subtitles pauses playback (if it was playing) so tracks
        // can be chosen without the video running on; closing the menu - by any
        // route - restores the state captured when it opened. mpv stays the
        // source of truth: the guard only remembers the user's pre-menu state.
        // =====================================================================

        private readonly MenuPlaybackGuard _menuGuard = new MenuPlaybackGuard();
        private bool _menuPausePending;

        // Bumped whenever a menu opens or closes, so an in-flight track
        // selection can tell the menu it started from is gone.
        private int _menuSerial;

        private async Task HoldPlaybackForMenuAsync()
        {
            PlayerSession session = _session;
            if (session == null || _disposed || _menuPausePending) return;

            _menuPausePending = true;

            try { await session.SetPausedAsync(true); }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Pausing for track menu failed: " + UserFacingErrors.ForLog(ex));
            }
            finally { _menuPausePending = false; }
        }

        // Resume after the menu closed - but only the playback it was opened
        // over: a late continuation must never resume another episode/source.
        private async Task ResumeAfterMenuAsync(PlaybackContext context, int generation)
        {
            PlayerSession session = _session;
            if (session == null || _disposed) return;
            if (!ReferenceEquals(session.Context, context) || session.PlayGeneration != generation) return;

            try { await session.SetPausedAsync(false); }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Resuming after track menu failed: " + UserFacingErrors.ForLog(ex));
            }
        }

        // Called with every state refresh while a track menu is open.
        private void EnforceMenuPlaybackHold(PlaybackState state)
        {
            if (!_menuGuard.IsHeld || _session == null) return;

            if (!_menuGuard.IsSamePlayback(_session.Context, _session.PlayGeneration))
            {
                // A different episode/source loaded under the open menu: drop the
                // menu without resuming anything (the new playback starts by
                // itself, as it always does).
                CloseActiveMenuAndRestoreFocus(restorePlayback: true);
                return;
            }

            // Still the same playback: it must stay paused while the menu is
            // open (this also covers a file that finishes loading under it).
            if (state.IsPlaybackEstablished && state.IsPlaying && !_menuPausePending)
            {
                var pauseTask = HoldPlaybackForMenuAsync();
            }
        }

        // The loaded file's tracks changed while a track menu is open (a new
        // episode/source was loaded, or mpv updated the list): rebuild the
        // menu from the new state so it never shows stale tracks.
        private void RefreshOpenTrackMenuIfStale()
        {
            if (_session == null || _focus == null) return;
            if (_uiState.OpenMenu != PlayerMenu.Audio && _uiState.OpenMenu != PlayerMenu.Subtitles) return;
            if (_session.State.TracksVersion == _trackMenuVersion) return;

            _focus.ReplaceActiveMenuGroup(BuildTrackMenuGroup(_pendingTrackMenuIsAudio));
        }

        // A track pick only asks mpv to switch (no restart, no reload, no
        // seek). Whether it worked is decided by mpv: PlayerSession reads the
        // real selection back, and a rejected/impossible request leaves the
        // previous selection in place and shows a short, generic notice (never
        // an mpv message or URL).
        private async void TrackMenu_TrackSelected(object sender, TrackMenuItemSelectedEventArgs e)
        {
            bool isAudio = _pendingTrackMenuIsAudio;
            bool applied = true;
            int menuSerial = _menuSerial;

            try
            {
                if (_session != null)
                {
                    if (isAudio)
                        applied = e.TrackId.HasValue && await _session.SelectAudioTrackAsync(e.TrackId.Value);
                    else
                        applied = await _session.SelectSubtitleTrackAsync(e.TrackId);
                }
            }
            catch (Exception ex)
            {
                applied = false;
                StremioVideoCenterLogger.Info("Track selection failed: " + UserFacingErrors.ForLog(ex));
            }

            // If the menu was already closed (or replaced) while the command was
            // in flight, there is nothing left to close or resume.
            if (menuSerial == _menuSerial && _uiState.OpenMenu != PlayerMenu.None)
                CloseActiveMenuAndRestoreFocus();

            if (!applied)
                ShowNotice(isAudio ? "Couldn\u2019t change the audio track" : "Couldn\u2019t change the subtitles");
        }

        // Subtitle delay is a session-only mpv adjustment: it is sent to mpv
        // (sub-delay) and nothing else - not Watch State, not the media
        // identity, not the subtitle file. The menu stays open so the user
        // can keep adjusting; the displayed value follows mpv's property.
        private async void TrackMenu_SubtitleDelayStepRequested(object sender, double deltaSeconds)
        {
            try { if (_session != null) await _session.AdjustSubtitleDelayAsync(deltaSeconds); }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Subtitle delay change failed: " + UserFacingErrors.ForLog(ex));
                ShowNotice("Couldn\u2019t change the subtitle delay");
            }
            ShowControls();
        }

        private async void TrackMenu_SubtitleDelayResetRequested(object sender, EventArgs e)
        {
            try { if (_session != null) await _session.ResetSubtitleDelayAsync(); }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Subtitle delay reset failed: " + UserFacingErrors.ForLog(ex));
                ShowNotice("Couldn\u2019t reset the subtitle delay");
            }
            ShowControls();
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
        // Pops the focus scope (which also restores focus to the button that
        // opened the menu), hides the menu surface, and THEN restores the
        // playback state captured when a track menu opened (restorePlayback:
        // false only when another menu immediately replaces this one).
        private void CloseActiveMenuAndRestoreFocus(bool restorePlayback = true)
        {
            bool wasTrackMenu =
                _uiState.OpenMenu == PlayerMenu.Audio || _uiState.OpenMenu == PlayerMenu.Subtitles;

            if (_focus != null)
                _focus.PopScope();

            MenuHost.Visibility = Visibility.Collapsed;
            MenuHost.Content = null;
            _uiState.OpenMenu = PlayerMenu.None;
            _menuSerial++;

            if (wasTrackMenu && restorePlayback && _session != null)
            {
                PlaybackContext context = _session.Context;
                int generation = _session.PlayGeneration;

                if (_menuGuard.Close(context, generation, _session.State.IsPlaying))
                {
                    var resumeTask = ResumeAfterMenuAsync(context, generation);
                }
            }

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

        // Keyboard model (controller input keeps D-pad focus navigation via
        // HandleControllerInput below):
        //   Space            play / pause (activates the focused item inside a menu)
        //   Enter            activate the focused control
        //   Left / Right     seek -/+10s (Volume slider focused: volume -/+5)
        //   Up / Down        volume +/-5
        //   M / F            mute / fullscreen
        //   Tab              move focus across the controls (standard WPF)
        //   Esc / Backspace / B   close menu, else leave an engaged slider,
        //                    else exit the player
        // While an Audio/Subtitles/Settings menu is open, the arrows move
        // focus within the menu instead.
        private void HandlePlayerKeyDown(KeyEventArgs e)
        {
            if (_disposed || _focus == null) return;

            // Any key is activity: reveal the overlay and restart the idle timer.
            ShowControls();

            bool inMenu = !_focus.IsAtRootScope;
            bool volumeFocused = ReferenceEquals(Keyboard.FocusedElement, VolumeSlider);

            switch (e.Key)
            {
                // Focus stays inside an open menu: Tab walks the menu items
                // instead of escaping to the control bar behind it.
                case Key.Tab:
                    if (inMenu)
                    {
                        _focus.MoveVertical(
                            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
                        e.Handled = true;
                    }
                    break;

                case Key.B:
                case Key.Escape:
                case Key.Back:
                    HandleClose();
                    e.Handled = true;
                    break;

                case Key.Enter:
                    HandleActivate();
                    e.Handled = true;
                    break;

                case Key.Space:
                    if (inMenu)
                        HandleActivate();
                    else
                        TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.Left:
                case Key.Right:
                {
                    int direction = e.Key == Key.Left ? -1 : 1;
                    if (inMenu)
                        _focus.MoveHorizontal(direction);
                    else if (volumeFocused)
                        AdjustVolume(direction * VolumeStep);
                    else
                    {
                        var ignored = SeekRelativeAsync(direction * SeekStepSeconds);
                    }
                    e.Handled = true;
                    break;
                }

                case Key.Up:
                case Key.Down:
                {
                    int direction = e.Key == Key.Up ? -1 : 1;
                    if (inMenu)
                        _focus.MoveVertical(direction);
                    else
                        AdjustVolume(-direction * VolumeStep);
                    e.Handled = true;
                    break;
                }

                // Episode navigation: dedicated keys, so Left/Right stay seek and
                // Space stays play/pause. A no-op unless the buttons are active.
                case Key.PageUp:
                case Key.PageDown:
                    if (!inMenu)
                    {
                        Button target = e.Key == Key.PageUp ? PreviousEpisodeButton : NextEpisodeButton;
                        if (target.IsEnabled && target.Visibility == Visibility.Visible)
                            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                    }
                    break;

                case Key.M:
                    if (!inMenu)
                    {
                        ToggleMute();
                        e.Handled = true;
                    }
                    break;

                case Key.F:
                    if (!inMenu)
                    {
                        ToggleFullscreen();
                        e.Handled = true;
                    }
                    break;
            }

            // An open menu owns the keyboard: nothing it does not use may leak
            // to the player controls behind it (system keys such as Alt are
            // left alone).
            if (inMenu && !e.Handled && e.Key != Key.System)
                e.Handled = true;
        }

        // Called by the Video Center's existing gamepad plumbing.
        public bool HandleControllerInput(ControllerInput button)
        {
            if (_disposed || _focus == null) return false;

            // Any controller button is activity.
            ShowControls();

            switch (button)
            {
                case ControllerInput.B:
                    HandleClose();
                    return true;

                case ControllerInput.A:
                    HandleActivate();
                    return true;

                case ControllerInput.X:
                    // Quick play/pause from anywhere on the bar. Not while a
                    // menu is open, where X has no meaning.
                    if (_focus.IsAtRootScope)
                    {
                        TogglePlayPause();
                        return true;
                    }
                    return false;

                case ControllerInput.DPadLeft:
                    _focus.MoveHorizontal(-1);
                    return true;

                case ControllerInput.DPadRight:
                    _focus.MoveHorizontal(1);
                    return true;

                case ControllerInput.DPadUp:
                    _focus.MoveVertical(-1);
                    return true;

                case ControllerInput.DPadDown:
                    _focus.MoveVertical(1);
                    return true;
            }

            return false;
        }

        // B / Escape is context-sensitive:
        //   - An engaged slider (volume) is released first.
        //   - A menu is open (Audio/Subtitle/Settings): pop it, restoring focus
        //     to the button that opened it, don't close the player.
        //   - Focus on a slider at the root scope: back to the default
        //     control (bottom Play/Pause), don't close the player.
        //   - Otherwise: exit the player (a manual stop - never marks the item
        //     completed; the view flushes the last valid progress).
        private void HandleClose()
        {
            UIElement focused = Keyboard.FocusedElement as UIElement;
            PlayerSlider slider = focused as PlayerSlider;

            switch (PlayerStateRules.BackAction(
                slider != null && slider.IsEngaged,
                !_focus.IsAtRootScope,
                slider != null))
            {
                case PlayerBackAction.ReleaseSlider:
                    slider.Disengage();
                    UpdateSliderEngagedVisual(slider);
                    return;

                case PlayerBackAction.CloseMenu:
                    // First Back only closes the menu (restoring the playback
                    // state captured when it opened); it never leaves the player.
                    CloseActiveMenuAndRestoreFocus();
                    return;

                case PlayerBackAction.FocusDefaultControl:
                    _focus.FocusRootDefault();
                    return;

                default:
                    RaiseCloseRequested();
                    return;
            }
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
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            // A on the volume slider engages/releases it for Left/Right
            // adjustment. On the seek bar it does nothing (Left/Right always
            // seek), avoiding an accidental play/pause while seeking.
            PlayerSlider slider = focused as PlayerSlider;
            if (slider != null && slider.RequiresEngagement)
            {
                slider.ToggleEngaged();
                UpdateSliderEngagedVisual(slider);
            }
        }

        // Makes "engaged" obvious: a lit track behind the slider.
        private static void UpdateSliderEngagedVisual(PlayerSlider slider)
        {
            slider.Background = slider.IsEngaged
                ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;
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
        // TRANSIENT OVERLAY (activity tracking + fade)
        //
        // The overlay is a popup (so it renders above the native mpv HWND) that
        // fades via Opacity instead of closing: closing the popup would drop
        // keyboard focus, and mouse movement over the video HWND never reaches
        // WPF, so the popup staying open also keeps activity detection working.
        // Visibility rules live in OverlayVisibilityPolicy.
        // =====================================================================

        private void Player_MouseMove(object sender, MouseEventArgs e)
        {
            ShowControls();
        }

        // Mouse movement/clicks on the overlay itself (including over the
        // control bar) are activity.
        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            ShowControls();
        }

        private void Overlay_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ShowControls();

            // A click outside an open menu closes it (and is consumed, so it
            // cannot also press whatever control sits behind the menu). Focus
            // returns to the button that opened it.
            if (_focus != null && !_focus.IsAtRootScope && !IsWithinMenu(e.OriginalSource as DependencyObject))
            {
                CloseActiveMenuAndRestoreFocus();
                e.Handled = true;
            }
        }

        private bool IsWithinMenu(DependencyObject source)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, MenuHost.Content))
                    return true;

                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }

            return false;
        }

        // Any interaction: restart the idle timer and make sure the overlay
        // is visible.
        private void ShowControls()
        {
            if (_disposed) return;

            _overlayPolicy.NoteActivity(DateTime.UtcNow);

            SyncControlsPopupSize();
            ControlsPopup.IsOpen = true;
            SetOverlayVisible(true);
        }

        // Retained name for the many "interaction happened" call sites; the
        // idle timer itself is now owned by OverlayVisibilityPolicy.
        private void RestartHideTimer()
        {
            if (_disposed) return;
            _overlayPolicy.NoteActivity(DateTime.UtcNow);
        }

        private void SetOverlayVisible(bool visible)
        {
            if (_overlayVisible == visible) return;

            _overlayVisible = visible;
            _uiState.ControlsVisible = visible;

            // A faded-out overlay must not intercept clicks meant for nothing.
            ControlsOverlay.IsHitTestVisible = visible;

            DoubleAnimation animation = new DoubleAnimation(
                visible ? 1.0 : 0.0,
                TimeSpan.FromMilliseconds(visible ? 150 : 450))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            ControlsOverlay.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void HideControlsTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed) return;

            PollCursorActivity();

            PlaybackState state = _session != null ? _session.State : null;

            bool playbackRunning =
                state != null && state.IsPlaybackEstablished && !state.ReachedEndOfFile;
            bool paused = state == null || !state.IsPlaying;

            bool visible = _overlayPolicy.ShouldBeVisible(
                DateTime.UtcNow,
                playbackRunning,
                paused,
                _uiState.OpenMenu != PlayerMenu.None,
                _uiState.IsSeekBarBeingDragged);

            SetOverlayVisible(visible);
        }

        // Samples the OS cursor: WPF never sees mouse movement over the native
        // mpv window, so this is what wakes the overlay when the mouse moves
        // over the video.
        private void PollCursorActivity()
        {
            NativePoint point;
            if (!GetCursorPos(out point)) return;

            bool moved = _haveCursorSample && (point.X != _lastCursorX || point.Y != _lastCursorY);

            _haveCursorSample = true;
            _lastCursorX = point.X;
            _lastCursorY = point.Y;

            if (moved && IsScreenPointOverPlayer(point))
                ShowControls();
        }

        private bool IsScreenPointOverPlayer(NativePoint point)
        {
            try
            {
                Point topLeft = RootGrid.PointToScreen(new Point(0, 0));
                Point bottomRight = RootGrid.PointToScreen(new Point(RootGrid.ActualWidth, RootGrid.ActualHeight));

                return point.X >= topLeft.X && point.X <= bottomRight.X &&
                       point.Y >= topLeft.Y && point.Y <= bottomRight.Y;
            }
            catch
            {
                // Not attached to a presentation source (yet/anymore).
                return false;
            }
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
