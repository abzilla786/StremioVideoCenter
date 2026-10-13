using Playnite.SDK.Events;
using StremioVideoCenter.Models;
using StremioVideoCenter.Services;
using StremioVideoCenterControls.Player;
using StremioVideoCenterControls.Player.Menus;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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

        // Slice 20 Stage 3: the full-page subtitle selector. It lives in the
        // same MenuHost as the other menus (PlayerMenu.Subtitles), so Back,
        // focus return and the pause/restore guard are shared with them.
        private readonly SubtitlePage _subtitlePage = new SubtitlePage();
        private CancellationTokenSource _subtitleDiscoveryCts;
        private int _subtitleDiscoverySerial;

        // Supplies the external subtitle search for the playing content
        // (set by the Video Center view, which owns the addon list). Called
        // only when the selector opens; null means "no external source".
        public Func<PlaybackContext, CancellationToken, Task<SubtitleDiscoveryResult>> ExternalSubtitleProvider { get; set; }

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

        // Raised for arrow keys that reach the controls overlay. The overlay is
        // its own PresentationSource (Popup or owned Window), so those keys never
        // pass through the Video Center view's PreviewKeyDown; the view uses this
        // to apply that same handler (controller-echo suppression and keyboard ->
        // D-pad routing) to overlay keys. See Overlay_PreviewKeyDown.
        public event KeyEventHandler OverlayKeyPreview;

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

            // The popup's native window only exists while open; the size hook
            // follows its lifetime (see AttachPopupSizeHook).
            ControlsPopup.Opened += ControlsPopup_Opened;
            ControlsPopup.Closed += ControlsPopup_Closed;

            RegisterOverlayToolTips();

            Loaded += MpvPlayer_Loaded;
            Unloaded += MpvPlayer_Unloaded;
            SizeChanged += MpvPlayer_SizeChanged;
            PreviewKeyDown += MpvPlayer_PreviewKeyDown;
            MouseMove += Player_MouseMove;

            _trackMenu.TrackSelected += TrackMenu_TrackSelected;
            _trackMenu.SubtitleDelayStepRequested += TrackMenu_SubtitleDelayStepRequested;
            _trackMenu.SubtitleDelayResetRequested += TrackMenu_SubtitleDelayResetRequested;

            _subtitlePage.OptionChosen += SubtitlePage_OptionChosen;
            _subtitlePage.OffChosen += SubtitlePage_OffChosen;
            _subtitlePage.BackRequested += SubtitlePage_BackRequested;
            _subtitlePage.DelayStepRequested += TrackMenu_SubtitleDelayStepRequested;
            _subtitlePage.DelayResetRequested += TrackMenu_SubtitleDelayResetRequested;

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
                // Details go to the log (URL-stripped). No dialog: this
                // attempt runs concurrently with PlayVideoAsync's own
                // EnsureStartedAsync (they share the startup gate, and the
                // second one retries), so the outcome that matters is
                // PlayVideoAsync's - if that also fails its caller closes
                // the player and shows a non-blocking notice; if it
                // succeeds there is nothing to report. A modal here used to
                // appear on top of (and in addition to) that handling.
                StremioVideoCenterLogger.Info(
                    "MpvPlayer_Loaded: startup failed - " + UserFacingErrors.ForLog(ex));
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

            // Click-to-seek: see SeekSlider_TrackPress. handledEventsToo is
            // required - the Slider itself marks a track press as handled.
            SeekSlider.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(SeekSlider_TrackPress),
                true);

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

                    // Cancellation passes through untouched. Anything else
                    // means the player process/IPC could not be brought
                    // up: tag it so callers can tell that apart from a
                    // file mpv refused to play. The original is kept as
                    // the inner exception for the log.
                    if (ex is OperationCanceledException)
                        throw;

                    throw new PlayerStartupException(ex);
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

            // No subtitle search outlives the player.
            CancelSubtitleDiscovery();

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
            CloseOverlayWindow();
            try { ControlsPopup.Opened -= ControlsPopup_Opened; } catch { }
            try { ControlsPopup.Closed -= ControlsPopup_Closed; } catch { }
            DetachPopupSizeHook();
            try { PreviewKeyDown -= MpvPlayer_PreviewKeyDown; } catch { }
            try { SizeChanged -= MpvPlayer_SizeChanged; } catch { }
            try { Unloaded -= MpvPlayer_Unloaded; } catch { }
            try { Loaded -= MpvPlayer_Loaded; } catch { }
            try { _trackMenu.TrackSelected -= TrackMenu_TrackSelected; } catch { }
            try { _trackMenu.SubtitleDelayStepRequested -= TrackMenu_SubtitleDelayStepRequested; } catch { }
            try { _trackMenu.SubtitleDelayResetRequested -= TrackMenu_SubtitleDelayResetRequested; } catch { }
            try { _subtitlePage.OptionChosen -= SubtitlePage_OptionChosen; } catch { }
            try { _subtitlePage.OffChosen -= SubtitlePage_OffChosen; } catch { }
            try { _subtitlePage.BackRequested -= SubtitlePage_BackRequested; } catch { }
            try { _subtitlePage.DelayStepRequested -= TrackMenu_SubtitleDelayStepRequested; } catch { }
            try { _subtitlePage.DelayResetRequested -= TrackMenu_SubtitleDelayResetRequested; } catch { }
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
            ScheduleOverlayCheck("host window changed");

            if (_overlayHostKind == OverlayHostKind.Popup && ControlsPopup.IsOpen)
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

            // Window host: the same visible-viewport rectangle, applied to the
            // overlay window instead of the popup (see OVERLAY WINDOW HOST).
            if (_overlayHostKind != OverlayHostKind.Popup)
            {
                SyncOverlayWindowBounds();
                return;
            }

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

            // A new nominal size invalidates any earlier "fit to the popup window"
            // measurement; otherwise cap at what the popup window really shows.
            if (Math.Abs(height - _lastNominalOverlayHeight) > 0.5 ||
                Math.Abs(width - _lastNominalOverlayWidth) > 0.5)
            {
                _lastNominalOverlayHeight = height;
                _lastNominalOverlayWidth = width;
                _fitMaxHeight = 0;
                _fitMaxWidth = 0;
            }

            // The size we WANT for the popup (before any fit cap); the size hook
            // restores it if WPF's own popup size restriction made the window smaller.
            _wantedPopupWidth = width;
            _wantedPopupHeight = height;

            if (_fitMaxHeight > 0) height = Math.Min(height, _fitMaxHeight);
            if (_fitMaxWidth > 0) width = Math.Min(width, _fitMaxWidth);

            if (Math.Abs(ControlsPopup.Width - width) < 0.5 &&
                Math.Abs(ControlsPopup.Height - height) < 0.5 &&
                Math.Abs(ControlsPopup.HorizontalOffset - offsetX) < 0.5 &&
                Math.Abs(ControlsPopup.VerticalOffset - offsetY) < 0.5)
            {
                return;
            }

            string resizeFrom = string.Format("from popup {0:0.#}x{1:0.#} offset=({2:0.##},{3:0.##}) to popup {4:0.#}x{5:0.#} offset=({6:0.##},{7:0.##})",
                ControlsPopup.Width, ControlsPopup.Height, ControlsPopup.HorizontalOffset, ControlsPopup.VerticalOffset,
                width, height, offsetX, offsetY);
            bool diagResize = ControlsPopup.IsOpen;

            ControlsPopup.HorizontalOffset = offsetX;
            ControlsPopup.VerticalOffset = offsetY;
            ControlsPopup.Width = width;
            ControlsPopup.Height = height;
            ControlsOverlay.Width = width;
            ControlsOverlay.Height = height;

            if (diagResize)
                LogPopupState("popup-resize " + resizeFrom + " | state after assignment");

            ScheduleOverlayCheck("size/offset changed");
        }

        // ---------------------------------------------------------------------
        // OVERLAY GEOMETRY CHECK (bottom control bar visibility)
        //
        // The overlay is hosted in a Popup, i.e. in its OWN native window. The
        // layout (Exit at the top, centre indicator in the middle, control bar
        // anchored to the bottom) is computed for ControlsOverlay.Height, but
        // only what falls inside that native window's client area is painted.
        // If the window turns out smaller than the layout, the bottom bar is
        // laid out - it has focus, shows tooltips - yet is never drawn.
        // After the popup is open this reads the popup window's real client
        // size, makes the layout fit it, and logs every figure involved (to
        // StremioVideoCenter.log) so the actual geometry can be read off.
        // ---------------------------------------------------------------------

        private double _lastNominalOverlayHeight;
        private double _lastNominalOverlayWidth;
        private double _fitMaxHeight;
        private double _fitMaxWidth;
        private bool _overlayCheckQueued;
        private string _lastOverlayLog;
        private int _overlayLogCount;

        private void ScheduleOverlayCheck(string reason)
        {
            if (_overlayCheckQueued || _disposed) return;

            _overlayCheckQueued = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _overlayCheckQueued = false;
                if (_disposed) return;

                try
                {
                    if (_overlayHostKind == OverlayHostKind.Popup)
                    {
                        FitOverlayToPopupWindow();
                        LogOverlayGeometry(reason);
                    }
                    else
                    {
                        LogOverlayWindowState(reason);
                    }
                }
                catch (Exception ex)
                {
                    StremioVideoCenterLogger.Info("Overlay geometry check failed: " + UserFacingErrors.ForLog(ex));
                }
            }), DispatcherPriority.Background);
        }

        private void FitOverlayToPopupWindow()
        {
            if (!ControlsPopup.IsOpen) return;

            HwndSource popupSource = PresentationSource.FromVisual(ControlsOverlay) as HwndSource;
            if (popupSource == null || popupSource.CompositionTarget == null) return;

            PixelRect? client = HostWindowGeometry.GetClientRectOnScreen(popupSource.Handle);
            if (!client.HasValue) return;

            Matrix fromDevice = popupSource.CompositionTarget.TransformFromDevice;
            double clientHeight = client.Value.Height * fromDevice.M22;
            double clientWidth = client.Value.Width * fromDevice.M11;

            // Fit the layout we WANT (not one a previous fit already shrank), so a
            // popup window that later reaches the wanted size releases the cap.
            double desiredHeight = _wantedPopupHeight > 0 ? _wantedPopupHeight : ControlsOverlay.Height;
            double desiredWidth = _wantedPopupWidth > 0 ? _wantedPopupWidth : ControlsOverlay.Width;

            double fittedHeight = OverlayFit.Constrain(desiredHeight, clientHeight);
            double fittedWidth = OverlayFit.Constrain(desiredWidth, clientWidth);

            double newMaxHeight = fittedHeight < desiredHeight - 0.5 ? fittedHeight : 0;
            double newMaxWidth = fittedWidth < desiredWidth - 0.5 ? fittedWidth : 0;

            if (Math.Abs(newMaxHeight - _fitMaxHeight) > 0.5 || Math.Abs(newMaxWidth - _fitMaxWidth) > 0.5)
            {
                _fitMaxHeight = newMaxHeight;
                _fitMaxWidth = newMaxWidth;
                SyncControlsPopupSize();
            }
        }

        private void LogOverlayGeometry(string reason)
        {
            if (_overlayLogCount >= 60) return;

            HwndSource popupSource = PresentationSource.FromVisual(ControlsOverlay) as HwndSource;
            PixelRect? popupWindow = popupSource != null ? HostWindowGeometry.GetWindowRectOnScreen(popupSource.Handle) : null;
            PixelRect? popupClient = popupSource != null ? HostWindowGeometry.GetClientRectOnScreen(popupSource.Handle) : null;

            PixelRect nominal;
            PixelRect? visible = GetVisiblePlayerBounds(out nominal);

            string bar = "n/a";
            try
            {
                Point topLeft = BottomControls.TranslatePoint(new Point(0, 0), ControlsOverlay);
                bar = string.Format(
                    "pos=({0:0},{1:0}) size={2:0}x{3:0} bottom={4:0} vis={5} opacity={6:0.##}",
                    topLeft.X, topLeft.Y, BottomControls.ActualWidth, BottomControls.ActualHeight,
                    topLeft.Y + BottomControls.ActualHeight, BottomControls.IsVisible, BottomControls.Opacity);
            }
            catch { }

            string text = string.Format(
                "popupOpen={0} popupSize={1:0}x{2:0} offset=({3:0.##},{4:0.##}) overlay={5:0}x{6:0} opacity={7:0.##} hit={8} " +
                "popupWindow={9} popupClient={10} rootGrid={11:0}x{12:0} nominal={13} visible={14} bottomBar[{15}] fitMax={16:0}x{17:0}",
                ControlsPopup.IsOpen, ControlsPopup.Width, ControlsPopup.Height,
                ControlsPopup.HorizontalOffset, ControlsPopup.VerticalOffset,
                ControlsOverlay.ActualWidth, ControlsOverlay.ActualHeight, ControlsOverlay.Opacity, ControlsOverlay.IsHitTestVisible,
                Describe(popupWindow), Describe(popupClient),
                RootGrid.ActualWidth, RootGrid.ActualHeight,
                Describe(nominal), Describe(visible), bar, _fitMaxWidth, _fitMaxHeight);

            if (text == _lastOverlayLog) return;

            _lastOverlayLog = text;
            _overlayLogCount++;

            StremioVideoCenterLogger.Info("[PLAYER-GEOMETRY] " + reason + ": " + text);
            LogPopupState("popup-state-after-" + reason.Replace(' ', '-').Replace('/', '-'));
        }

        private static string Describe(PixelRect? rect)
        {
            return rect.HasValue
                ? string.Format("({0:0},{1:0},{2:0},{3:0})[{4:0}x{5:0}]", rect.Value.Left, rect.Value.Top, rect.Value.Right, rect.Value.Bottom, rect.Value.Width, rect.Value.Height)
                : "n/a";
        }

        // ---------------------------------------------------------------------
        // OVERLAY WINDOW HOST (reversible spike; replaces the Popup host)
        //
        // A WPF Popup is limited to 75% of the screen area, which left the
        // controls ~25% short of the viewport. The same ControlsOverlay is
        // instead hosted in an owned, borderless, transparent WPF Window that is
        // positioned on the visible player rectangle in physical pixels with the
        // existing VisibleBounds / HostWindowGeometry code. A top-level owned
        // window is always above its owner, so it stays above the native mpv
        // child window exactly like the Popup did. Nothing about mpv, the host
        // window or the controls themselves changes.
        //
        // Rollback without rebuilding: set the environment variable
        // ZINO_PLAYER_OVERLAY=popup (window-active makes the window activatable).
        // ---------------------------------------------------------------------

        private static readonly OverlayHostKind _overlayHostKind =
            OverlayHostMode.Parse(Environment.GetEnvironmentVariable(OverlayHostMode.EnvironmentVariable));

        private OverlayHostWindow _overlayWindow;
        private int _overlayWindowLogCount;
        private string _lastOverlayWindowLog;

        // Owned, borderless, transparent, never in the taskbar or Alt+Tab.
        private sealed class OverlayHostWindow : Window
        {
            private const int GwlExStyle = -20;
            private readonly bool _noActivate;

            public OverlayHostWindow(bool noActivate)
            {
                _noActivate = noActivate;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ShowInTaskbar = false;
                ShowActivated = false;
                Topmost = false;
                SizeToContent = SizeToContent.Manual;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            protected override void OnSourceInitialized(EventArgs e)
            {
                base.OnSourceInitialized(e);

                try
                {
                    IntPtr handle = new WindowInteropHelper(this).Handle;
                    long current = GetExStyle(handle);
                    long updated = OverlayWindowStyle.Apply(current, _noActivate);
                    if (updated != current)
                        SetExStyle(handle, updated);
                }
                catch { }
            }

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
            private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
            private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

            private static long GetExStyle(IntPtr handle)
            {
                return IntPtr.Size == 8
                    ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
                    : GetWindowLong32(handle, GwlExStyle);
            }

            private static void SetExStyle(IntPtr handle, long value)
            {
                if (IntPtr.Size == 8)
                    SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(value));
                else
                    SetWindowLong32(handle, GwlExStyle, unchecked((int)value));
            }
        }

        // Creates the overlay window (once) and moves ControlsOverlay out of the
        // popup into it. Needs the host window to exist and be shown, so it is
        // created lazily on first use; returns false until then.
        private bool EnsureOverlayWindow()
        {
            if (_overlayWindow != null) return true;
            if (_disposed) return false;

            Window owner = Window.GetWindow(this);
            if (owner == null || !owner.IsVisible) return false;

            try
            {
                OverlayHostWindow window = new OverlayHostWindow(_overlayHostKind != OverlayHostKind.ActivatableWindow);

                // The popup child inherited these from the UserControl; a window
                // would not, so carry them over to keep the controls identical.
                window.FontFamily = FontFamily;
                window.FontSize = FontSize;
                window.FontWeight = FontWeight;
                window.FontStyle = FontStyle;
                window.Foreground = Foreground;
                window.FlowDirection = FlowDirection;
                window.UseLayoutRounding = UseLayoutRounding;

                ControlsPopup.IsOpen = false;
                ControlsPopup.Child = null;

                // The overlay now fills whatever size the window is given.
                ControlsOverlay.ClearValue(FrameworkElement.WidthProperty);
                ControlsOverlay.ClearValue(FrameworkElement.HeightProperty);

                window.Content = ControlsOverlay;
                window.Owner = owner;

                _overlayWindow = window;
                return true;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Overlay window creation failed: " + UserFacingErrors.ForLog(ex));
                return false;
            }
        }

        // Applies the visible player rectangle (physical pixels) to the overlay
        // window. Same rectangle the MPV host is laid out in; no second geometry
        // system, and no DIP round trip.
        private void SyncOverlayWindowBounds()
        {
            if (_overlayWindow == null || RootGrid == null || _disposed)
                return;

            try
            {
                PixelRect nominal;
                PixelRect? visible = GetVisiblePlayerBounds(out nominal);

                PixelRect target;
                if (visible.HasValue && !visible.Value.IsEmpty)
                    target = visible.Value;
                else if (!nominal.IsEmpty)
                    target = nominal;
                else
                    return;

                IntPtr handle = new WindowInteropHelper(_overlayWindow).EnsureHandle();
                PixelRect? current = HostWindowGeometry.GetWindowRectOnScreen(handle);

                if (current.HasValue && !HostWindowGeometry.NeedsUpdate(current.Value, target))
                    return;

                HostWindowGeometry.SetWindowRect(handle, target);
                ScheduleOverlayCheck("overlay window bounds");
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Overlay window sync failed: " + UserFacingErrors.ForLog(ex));
            }
        }

        // Opens the controls host: the popup, or the overlay window.
        private void OpenControlsHost()
        {
            if (_disposed) return;

            if (_overlayHostKind == OverlayHostKind.Popup)
            {
                ControlsPopup.IsOpen = true;
                return;
            }

            if (!EnsureOverlayWindow()) return;

            SyncOverlayWindowBounds();

            if (_overlayWindow.IsVisible) return;

            try
            {
                _overlayWindow.Show();
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Overlay window show failed: " + UserFacingErrors.ForLog(ex));
                return;
            }

            // Show can re-apply WPF's own default placement; the physical-pixel
            // rectangle is authoritative.
            SyncOverlayWindowBounds();

            if (_overlayHostKind == OverlayHostKind.ActivatableWindow)
            {
                try { _overlayWindow.Activate(); } catch { }
            }

            LogDisplayDiagnostics();
            ScheduleOverlayCheck("overlay window opened");
        }

        private void CloseOverlayWindow()
        {
            OverlayHostWindow window = _overlayWindow;
            _overlayWindow = null;

            if (window == null) return;

            try { window.Content = null; } catch { }
            try { window.Owner = null; } catch { }
            try { window.Close(); } catch { }
        }

        // Diagnostics for the window host (a window rect and client rect that
        // match the visible rectangle show the Popup limit is gone).
        private void LogOverlayWindowState(string reason)
        {
            if (_overlayWindowLogCount >= 40 || _overlayWindow == null) return;

            try
            {
                HwndSource source = PresentationSource.FromVisual(ControlsOverlay) as HwndSource;
                IntPtr handle = source != null ? source.Handle : IntPtr.Zero;
                Window owner = _overlayWindow.Owner;
                IntPtr ownerHandle = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;

                PixelRect nominal;
                PixelRect? visible = GetVisiblePlayerBounds(out nominal);

                string bar = "n/a";
                try
                {
                    Point topLeft = BottomControls.TranslatePoint(new Point(0, 0), ControlsOverlay);
                    bar = string.Format("top={0:0} bottom={1:0} vis={2}",
                        topLeft.Y, topLeft.Y + BottomControls.ActualHeight, BottomControls.IsVisible);
                }
                catch { }

                double scaleX = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 0;

                string text = string.Format(
                    "overlay-window {0}: kind={1} visible={2} window={3} client={4} owner={5} nominal={6} visibleRect={7} " +
                    "overlayActual={8:0}x{9:0} bottomBar[{10}] dpi={11} scale={12:0.###} opacity={13:0.##}",
                    reason, _overlayHostKind, _overlayWindow.IsVisible,
                    Describe(handle != IntPtr.Zero ? HostWindowGeometry.GetWindowRectOnScreen(handle) : null),
                    Describe(handle != IntPtr.Zero ? HostWindowGeometry.GetClientRectOnScreen(handle) : null),
                    Describe(ownerHandle != IntPtr.Zero ? HostWindowGeometry.GetWindowRectOnScreen(ownerHandle) : null),
                    Describe(nominal), Describe(visible),
                    ControlsOverlay.ActualWidth, ControlsOverlay.ActualHeight, bar,
                    SafeDpi(handle), scaleX, ControlsOverlay.Opacity);

                if (text == _lastOverlayWindowLog) return;

                _lastOverlayWindowLog = text;
                _overlayWindowLogCount++;
                StremioVideoCenterLogger.Info("[PLAYER-GEOMETRY] " + text);
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("[PLAYER-GEOMETRY] overlay-window log failed: " + UserFacingErrors.ForLog(ex));
            }
        }

        // ---------------------------------------------------------------------
        // POPUP SIZE HOOK
        //
        // WPF restricts a Popup's native window to a fraction of the screen
        // (RestrictSize), so the controls popup is created smaller than the size
        // requested, and the bottom-anchored control bar then sits too high.
        // The hook watches the popup window's WM_WINDOWPOSCHANGING and, when WPF
        // is about to apply a size smaller than the wanted one, raises cx/cy to
        // the wanted size. Position (x/y) is never touched, nothing is ever
        // shrunk, and SetWindowPos is not called. OverlayFit stays as the
        // fallback if the window is still smaller than requested.
        // ---------------------------------------------------------------------

        private const int WmWindowPosChanging = 0x0046;
        private const uint SwpNoSize = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPos
        {
            public IntPtr Hwnd;
            public IntPtr HwndInsertAfter;
            public int X;
            public int Y;
            public int Cx;
            public int Cy;
            public uint Flags;
        }

        private HwndSource _popupHookSource;
        private HwndSourceHook _popupHook;
        private double _wantedPopupWidth;
        private double _wantedPopupHeight;
        private bool _inPopupHook;
        private int _popupHookLogCount;
        private bool _popupHookAfterQueued;

        private void ControlsPopup_Opened(object sender, EventArgs e)
        {
            if (_disposed) return;

            AttachPopupSizeHook();

            // The first sizing already happened before the window could be
            // hooked; a re-placement makes WPF apply the size again (through the
            // hook). Same sub-pixel nudge HostWindow_GeometryChanged relies on.
            try
            {
                _popupNudge = !_popupNudge;
                ControlsPopup.HorizontalOffset += _popupNudge ? 0.01 : -0.01;
            }
            catch { }

            ScheduleOverlayCheck("popup hooked");
        }

        private void ControlsPopup_Closed(object sender, EventArgs e)
        {
            DetachPopupSizeHook();
        }

        private void AttachPopupSizeHook()
        {
            try
            {
                HwndSource source = PresentationSource.FromVisual(ControlsOverlay) as HwndSource;
                if (source == null || source.IsDisposed) return;
                if (ReferenceEquals(source, _popupHookSource)) return;

                DetachPopupSizeHook();

                _popupHook = PopupWndProc;
                source.AddHook(_popupHook);
                _popupHookSource = source;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Popup size hook attach failed: " + UserFacingErrors.ForLog(ex));
            }
        }

        private void DetachPopupSizeHook()
        {
            try
            {
                if (_popupHookSource != null && _popupHook != null && !_popupHookSource.IsDisposed)
                    _popupHookSource.RemoveHook(_popupHook);
            }
            catch { }

            _popupHookSource = null;
            _popupHook = null;
        }

        private IntPtr PopupWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmWindowPosChanging || _inPopupHook || _disposed || lParam == IntPtr.Zero)
                return IntPtr.Zero;

            _inPopupHook = true;
            try
            {
                NativeWindowPos pos = (NativeWindowPos)Marshal.PtrToStructure(lParam, typeof(NativeWindowPos));
                if ((pos.Flags & SwpNoSize) != 0)
                    return IntPtr.Zero;

                HwndSource source = _popupHookSource;
                if (source == null || source.CompositionTarget == null)
                    return IntPtr.Zero;

                Matrix toDevice = source.CompositionTarget.TransformToDevice;
                int width;
                int height;

                bool rewrite = PopupSizeOverride.TryCompute(
                    pos.Cx, pos.Cy, _wantedPopupWidth, _wantedPopupHeight,
                    toDevice.M11, toDevice.M22, out width, out height);

                if (_popupHookLogCount < 30)
                {
                    _popupHookLogCount++;
                    PopupDiag(string.Format(
                        "popup-hook WINDOWPOS requested={0}x{1} at ({2},{3}) flags=0x{4:X} wanted={5:0.#}x{6:0.#}dip scale={7:0.###}x{8:0.###} rewritten={9}",
                        pos.Cx, pos.Cy, pos.X, pos.Y, pos.Flags, _wantedPopupWidth, _wantedPopupHeight,
                        toDevice.M11, toDevice.M22,
                        rewrite ? (width + "x" + height) : "no (unchanged)"));
                }

                if (rewrite)
                {
                    pos.Cx = width;
                    pos.Cy = height;
                    Marshal.StructureToPtr(pos, lParam, false);
                    ScheduleHookResultLog();
                }
            }
            catch (Exception ex)
            {
                PopupDiag("popup-hook failed: " + UserFacingErrors.ForLog(ex));
            }
            finally
            {
                _inPopupHook = false;
            }

            return IntPtr.Zero;
        }

        // Logs the popup window once WPF has finished positioning it.
        private void ScheduleHookResultLog()
        {
            if (_popupHookAfterQueued || _disposed) return;

            _popupHookAfterQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _popupHookAfterQueued = false;
                if (!_disposed) LogPopupState("popup-after-hook");
            }), DispatcherPriority.Background);
        }

        // ---------------------------------------------------------------------
        // POPUP CREATION DIAGNOSTICS (logging only; changes no behaviour)
        //
        // Finds what limits the ControlsPopup native window: the requested size,
        // the monitor / work area, desktop metrics, DPI, or WPF itself. Markers:
        // monitor / workArea / dpi / popup-pre-show / popup-post-show /
        // popup-resize. Capped so it cannot flood the log.
        // ---------------------------------------------------------------------

        private int _popupDiagCount;
        private const int PopupDiagLimit = 60;

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const uint MonitorDefaultToPrimary = 1;

        private static string DescribeNative(NativeRect r)
        {
            return string.Format("({0},{1},{2},{3})[{4}x{5}]", r.Left, r.Top, r.Right, r.Bottom, r.Right - r.Left, r.Bottom - r.Top);
        }

        private static string DescribeMonitor(IntPtr monitor, out string work)
        {
            NativeMonitorInfo info = new NativeMonitorInfo { Size = Marshal.SizeOf(typeof(NativeMonitorInfo)) };
            work = "n/a";
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return "n/a";
            work = DescribeNative(info.Work);
            return DescribeNative(info.Monitor) + (((info.Flags & 1) != 0) ? " primary" : string.Empty);
        }

        private static string SafeDpi(IntPtr hwnd)
        {
            try { return hwnd == IntPtr.Zero ? "n/a" : GetDpiForWindow(hwnd).ToString(); }
            catch { return "n/a"; }
        }

        private void PopupDiag(string text)
        {
            if (_popupDiagCount >= PopupDiagLimit) return;
            _popupDiagCount++;
            StremioVideoCenterLogger.Info("[PLAYER-GEOMETRY] " + text);
        }

        // Monitors, work areas, desktop metrics and DPI. Logged once per popup open.
        private void LogDisplayDiagnostics()
        {
            try
            {
                PixelRect nominal;
                GetVisiblePlayerBounds(out nominal);

                NativePoint centre = new NativePoint
                {
                    X = (int)((nominal.Left + nominal.Right) / 2),
                    Y = (int)((nominal.Top + nominal.Bottom) / 2)
                };
                NativePoint origin = new NativePoint { X = 0, Y = 0 };

                string playerWork;
                string playerMonitor = DescribeMonitor(MonitorFromPoint(centre, MonitorDefaultToNearest), out playerWork);
                string primaryWork;
                string primaryMonitor = DescribeMonitor(MonitorFromPoint(origin, MonitorDefaultToPrimary), out primaryWork);

                PopupDiag("monitor player=" + playerMonitor + " primary=" + primaryMonitor + " nominal=" + Describe(nominal));
                PopupDiag("workArea player=" + playerWork + " primary=" + primaryWork);

                System.Text.StringBuilder all = new System.Text.StringBuilder();
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr m, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    string w;
                    all.Append(DescribeMonitor(m, out w)).Append(" work=").Append(w).Append("; ");
                    return true;
                }, IntPtr.Zero);
                PopupDiag("monitor all=" + all);

                PopupDiag(string.Format(
                    "desktop SystemParameters primary={0:0}x{1:0} workArea={2:0}x{3:0} virtual=({4:0},{5:0}){6:0}x{7:0} fullPrimary={8:0}x{9:0} (WPF DIPs)",
                    SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight,
                    SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height,
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight,
                    SystemParameters.FullPrimaryScreenWidth, SystemParameters.FullPrimaryScreenHeight));

                Window window = Window.GetWindow(this);
                IntPtr hostHandle = window != null ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
                DpiScale playerDpi = VisualTreeHelper.GetDpi(RootGrid);
                PresentationSource playerSource = PresentationSource.FromVisual(RootGrid);
                double toDeviceX = playerSource != null && playerSource.CompositionTarget != null ? playerSource.CompositionTarget.TransformToDevice.M11 : 0;
                double toDeviceY = playerSource != null && playerSource.CompositionTarget != null ? playerSource.CompositionTarget.TransformToDevice.M22 : 0;

                PopupDiag(string.Format(
                    "dpi player scale={0:0.###}x{1:0.###} transformToDevice={2:0.###}x{3:0.###} hostWindowDpi={4} hostWindowRect={5}",
                    playerDpi.DpiScaleX, playerDpi.DpiScaleY, toDeviceX, toDeviceY, SafeDpi(hostHandle),
                    Describe(hostHandle != IntPtr.Zero ? HostWindowGeometry.GetWindowRectOnScreen(hostHandle) : null)));
            }
            catch (Exception ex)
            {
                PopupDiag("display diagnostics failed: " + UserFacingErrors.ForLog(ex));
            }
        }

        // Requested size (our Width/Height/offset), the popup native window and
        // its client rect, and the popup window's own DPI.
        private void LogPopupState(string marker)
        {
            try
            {
                HwndSource popupSource = PresentationSource.FromVisual(ControlsOverlay) as HwndSource;
                IntPtr handle = popupSource != null ? popupSource.Handle : IntPtr.Zero;
                PixelRect? win = handle != IntPtr.Zero ? HostWindowGeometry.GetWindowRectOnScreen(handle) : null;
                PixelRect? client = handle != IntPtr.Zero ? HostWindowGeometry.GetClientRectOnScreen(handle) : null;
                double scaleX = popupSource != null && popupSource.CompositionTarget != null ? popupSource.CompositionTarget.TransformToDevice.M11 : 0;
                double scaleY = popupSource != null && popupSource.CompositionTarget != null ? popupSource.CompositionTarget.TransformToDevice.M22 : 0;

                FrameworkElement popupRoot = popupSource != null ? popupSource.RootVisual as FrameworkElement : null;
                string rootSize = popupRoot != null ? string.Format("{0:0}x{1:0}", popupRoot.ActualWidth, popupRoot.ActualHeight) : "n/a";

                PopupDiag(string.Format(
                    "{0} requested=popup {1:0.#}x{2:0.#} overlay {3:0.#}x{4:0.#} offset=({5:0.##},{6:0.##}) isOpen={7} " +
                    "nativeWindow={8} client={9} popupDpi={10} popupTransformToDevice={11:0.###}x{12:0.###} fitMax={13:0}x{14:0} popupRoot={15} overlayActual={16:0}x{17:0}",
                    marker, ControlsPopup.Width, ControlsPopup.Height, ControlsOverlay.Width, ControlsOverlay.Height,
                    ControlsPopup.HorizontalOffset, ControlsPopup.VerticalOffset, ControlsPopup.IsOpen,
                    Describe(win), Describe(client), SafeDpi(handle), scaleX, scaleY, _fitMaxWidth, _fitMaxHeight,
                    rootSize, ControlsOverlay.ActualWidth, ControlsOverlay.ActualHeight));
            }
            catch (Exception ex)
            {
                PopupDiag(marker + " failed: " + UserFacingErrors.ForLog(ex));
            }
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
                _subtitlePage.UpdateDelay(state.SubtitleDelaySeconds);
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

                ScheduleOverlayCheck("playback established");

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

            SetOverlayToolTip(PreviousEpisodeButton, episode.HasPrevious
                ? "Previous episode \u2014 " + EpisodeContext.FormatCode(episode.Previous)
                : "No previous episode");
            SetOverlayToolTip(NextEpisodeButton, episode.HasNext
                ? "Next episode \u2014 " + EpisodeContext.FormatCode(episode.Next)
                : "No next episode");

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

        // A plain click on the bar (not on the thumb). With IsMoveToPointEnabled
        // the Slider jumps its Value to the click point inside its own
        // PreviewMouseLeftButtonDown handling and marks the event handled.
        // WPF shares one event-args object between PreviewMouseDown and
        // PreviewMouseLeftButtonDown, so the PreviewMouseDown handler above
        // is then never invoked: the "dragging" state was never entered and
        // the mouse-up commit returned immediately - the thumb jumped, but no
        // seek was sent and the next position update moved it back. Pressing
        // the thumb itself is not handled by the Slider, so that path (and
        // drag-to-seek) is unchanged. Here the already-updated Value is
        // committed through the same CommitSeekAsync used by a drag, which
        // also clears the state in its finally block, so a click can never
        // leave the seek bar stuck in "seeking".
        private void SeekSlider_TrackPress(object sender, MouseButtonEventArgs e)
        {
            if (!e.Handled ||
                e.ChangedButton != MouseButton.Left ||
                _uiState.IsSeekBarBeingDragged ||
                _disposed ||
                _session == null ||
                _session.State.DurationSeconds <= 0)
            {
                return;
            }

            _uiState.IsSeekBarBeingDragged = true;
            ShowControls();
            RestartHideTimer();

            var ignored = CommitSeekAsync();
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
            OpenSubtitlePage();
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
            if (_session.State.TracksVersion == _trackMenuVersion) return;

            // The subtitle page redraws itself in place (focus stays on the
            // same item when it still exists).
            if (_uiState.OpenMenu == PlayerMenu.Subtitles)
            {
                FeedSubtitlePage();
                _subtitlePage.Refresh();
                return;
            }

            if (_uiState.OpenMenu != PlayerMenu.Audio) return;

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

        // =====================================================================
        // SUBTITLE SELECTOR (Slice 20 Stage 3)
        //
        // Opens exactly like the Audio menu did - same guard captures whether
        // mpv was playing and pauses only then, same close path resumes only
        // then - but shows the full-page, language-first SubtitlePage. The
        // embedded tracks are shown at once from mpv's track list; the addon
        // search runs in the background and fills in the external side. A
        // failing or slow search never blocks the embedded options.
        // =====================================================================

        private void OpenSubtitlePage()
        {
            if (_session == null || _focus == null) return;

            // Already open: a second request must not start a second
            // session (it would re-capture the already-paused state as the
            // "before" state and the video would never resume).
            if (_uiState.OpenMenu == PlayerMenu.Subtitles) return;

            if (!_focus.IsAtRootScope)
                CloseActiveMenuAndRestoreFocus(restorePlayback: false);

            PlaybackState state = _session.State;
            if (_menuGuard.Open(
                    state.IsPlaybackEstablished && state.IsPlaying,
                    _session.Context,
                    _session.PlayGeneration))
            {
                var pauseTask = HoldPlaybackForMenuAsync();
            }

            _subtitlePage.Reset();
            FeedSubtitlePage();
            _subtitlePage.SetExternalLoading();
            _subtitlePage.Refresh();

            _menuSerial++;

            MenuHost.Content = _subtitlePage;
            MenuHost.Visibility = Visibility.Visible;
            _uiState.OpenMenu = PlayerMenu.Subtitles;

            _focus.PushMenuScope(_subtitlePage.CreateFocusGroup(), SubtitleButton);
            ShowControls();
            RestartHideTimer();

            StartSubtitleDiscovery();
        }

        // mpv's current subtitle state into the page.
        private void FeedSubtitlePage()
        {
            PlaybackState state = _session.State;

            _trackMenuVersion = state.TracksVersion;

            _subtitlePage.SetTracks(state.SubtitleTracks, state.SelectedSubtitleTrackId, state.TracksLoaded);
            _subtitlePage.SetLoadedExternal(_session.LoadedExternalSubtitles);
            _subtitlePage.UpdateDelay(state.SubtitleDelaySeconds);
        }

        private void CancelSubtitleDiscovery()
        {
            CancellationTokenSource cts = _subtitleDiscoveryCts;
            _subtitleDiscoveryCts = null;
            _subtitleDiscoverySerial++;

            if (cts == null) return;

            // Cancel only: the token has no timers or handles to release,
            // and a request still holding it must not see it disposed.
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async void StartSubtitleDiscovery()
        {
            CancelSubtitleDiscovery();

            PlayerSession session = _session;
            Func<PlaybackContext, CancellationToken, Task<SubtitleDiscoveryResult>> provider = ExternalSubtitleProvider;
            PlaybackContext context = session != null ? session.Context : null;

            if (session == null || provider == null || context == null)
            {
                _subtitlePage.SetExternalResult(new SubtitleDiscoveryResult { ContentUnidentified = true });
                _subtitlePage.Refresh();
                return;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            _subtitleDiscoveryCts = cts;
            int serial = _subtitleDiscoverySerial;
            int generation = session.PlayGeneration;

            SubtitleDiscoveryResult result = null;
            bool failed = false;

            try
            {
                result = await provider(context, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                failed = true;
                StremioVideoCenterLogger.Info("Subtitle discovery failed: " + UserFacingErrors.ForLog(ex));
            }

            // Stale-result protection: only the search started by THIS open
            // of the selector, for THIS playback, may touch the page.
            if (_disposed ||
                cts.IsCancellationRequested ||
                serial != _subtitleDiscoverySerial ||
                _uiState.OpenMenu != PlayerMenu.Subtitles ||
                _session == null ||
                !ReferenceEquals(_session.Context, context) ||
                _session.PlayGeneration != generation)
            {
                return;
            }

            if (failed || result == null)
            {
                _subtitlePage.SetExternalFailed();
            }
            else
            {
                _subtitlePage.SetExternalResult(result);

                // Counts only: never addon addresses, subtitle links or
                // provider responses.
                StremioVideoCenterLogger.Info(string.Format(
                    "Subtitle discovery: {0} addon(s) answered, {1} subtitle(s).",
                    result.PerAddon != null ? result.PerAddon.Count : 0,
                    result.Subtitles != null ? result.Subtitles.Count : 0));
            }

            FeedSubtitlePage();
            _subtitlePage.Refresh();
        }

        private void SubtitlePage_BackRequested(object sender, EventArgs e)
        {
            if (_uiState.OpenMenu == PlayerMenu.Subtitles)
                CloseActiveMenuAndRestoreFocus();
        }

        private async void SubtitlePage_OffChosen(object sender, EventArgs e)
        {
            await ApplyEmbeddedSubtitleSelectionAsync(null);
        }

        private async void SubtitlePage_OptionChosen(object sender, SubtitleOptionChosenEventArgs e)
        {
            SubtitleOption option = e.Option;
            if (option == null || _session == null || _uiState.OpenMenu != PlayerMenu.Subtitles) return;

            // Embedded, or an external subtitle mpv already holds: just a
            // track switch.
            if (!option.NeedsLoading)
            {
                await ApplyEmbeddedSubtitleSelectionAsync(option.TrackId);
                return;
            }

            if (_subtitlePage.IsBusy) return;

            int menuSerial = _menuSerial;
            ExternalSubtitleOutcome outcome = ExternalSubtitleOutcome.Failed;

            _subtitlePage.SetBusy("Adding subtitle…");

            try
            {
                outcome = await _session.ActivateExternalSubtitleAsync(
                    option.ExternalKey,
                    option.ExternalUrl,
                    option.SourceLabel,
                    option.RawLanguageCode);
            }
            catch (Exception ex)
            {
                // Fixed wording below; the exception text is not shown, and
                // is logged redacted.
                StremioVideoCenterLogger.Info("External subtitle activation failed: " + UserFacingErrors.ForLog(ex));
            }

            if (_disposed) return;

            bool pageStillOurs = menuSerial == _menuSerial && _uiState.OpenMenu == PlayerMenu.Subtitles;

            if (outcome == ExternalSubtitleOutcome.Superseded)
            {
                // A different file loaded meanwhile; the guard has already
                // dismissed the page.
                return;
            }

            if (!pageStillOurs)
            {
                // The user went Back while mpv was fetching it: nothing to
                // update, but a failure is still worth one quiet notice.
                if (outcome == ExternalSubtitleOutcome.Failed)
                    ShowNotice("Couldn\u2019t load that subtitle");
                return;
            }

            if (outcome == ExternalSubtitleOutcome.Activated)
            {
                CloseActiveMenuAndRestoreFocus();
                return;
            }

            // Failed (or busy): playback carries on untouched and the page
            // stays open so another option, or an embedded one, can be tried.
            FeedSubtitlePage();
            _subtitlePage.ClearBusy(outcome == ExternalSubtitleOutcome.Busy
                ? "Another subtitle is still being added."
                : "Couldn\u2019t load that subtitle. Playback is unaffected \u2014 try another option.");
            _subtitlePage.Refresh();
        }

        // Track switch within the running file (no reload, no seek): the
        // same mpv call the Audio/Subtitles menu always made, with the real
        // selection read back by PlayerSession.
        private async Task ApplyEmbeddedSubtitleSelectionAsync(int? trackId)
        {
            if (_session == null || _uiState.OpenMenu != PlayerMenu.Subtitles) return;

            int menuSerial = _menuSerial;
            bool applied = false;

            try
            {
                applied = await _session.SelectSubtitleTrackAsync(trackId);
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info("Track selection failed: " + UserFacingErrors.ForLog(ex));
            }

            if (_disposed || menuSerial != _menuSerial || _uiState.OpenMenu != PlayerMenu.Subtitles) return;

            if (applied)
            {
                CloseActiveMenuAndRestoreFocus();
                return;
            }

            FeedSubtitlePage();
            _subtitlePage.ClearBusy("Couldn\u2019t change the subtitles.");
            _subtitlePage.Refresh();
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

            if (_uiState.OpenMenu == PlayerMenu.Subtitles)
            {
                // Leaving the selector ends its search; a late answer can
                // no longer reach it (see StartSubtitleDiscovery).
                CancelSubtitleDiscovery();
                _subtitlePage.Reset();
            }

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
            bool directional =
                e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down;

            KeyEventHandler host = OverlayKeyPreview;
            bool forwarded = PlayerStateRules.ShouldForwardKeyToHost(directional, host != null);

            if (forwarded)
                host(this, e);

            if (PlayerStateRules.ShouldHandleKeyLocally(forwarded, e.Handled))
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
                    OpenControlsHost();
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
        private void ShowControls([CallerMemberName] string caller = null)
        {
            if (_disposed) return;

            NoteOverlayActivity("ShowControls<-" + caller);

            SyncControlsPopupSize();

            if (_overlayHostKind != OverlayHostKind.Popup)
            {
                OpenControlsHost();
                SetOverlayVisible(true, "ShowControls<-" + caller);
                return;
            }

            bool wasOpen = ControlsPopup.IsOpen;

            if (!wasOpen)
            {
                LogDisplayDiagnostics();
                LogPopupState("popup-pre-show");
            }

            ControlsPopup.IsOpen = true;

            // The popup window only exists once it is open: measure it then.
            if (!wasOpen)
            {
                // Captured before the Background-priority fit runs, so the
                // popup's original size is on record.
                LogPopupState("popup-post-show");
                Dispatcher.BeginInvoke(new Action(() => LogPopupState("popup-post-show-loaded")), DispatcherPriority.Loaded);
                ScheduleOverlayCheck("popup opened");
            }

            SetOverlayVisible(true, "ShowControls<-" + caller);
        }

        // Retained name for the many "interaction happened" call sites; the
        // idle timer itself is now owned by OverlayVisibilityPolicy.
        private void RestartHideTimer([CallerMemberName] string caller = null)
        {
            if (_disposed) return;
            NoteOverlayActivity("RestartHideTimer<-" + caller);
        }

        private void SetOverlayVisible(bool visible, string trigger = null)
        {
            if (_overlayVisible == visible) return;

            _overlayVisible = visible;

            LogFadeTransition(visible, trigger);
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

            FadeOverlayDecorations(visible, animation);
        }

        // =====================================================================
        // FADE: things drawn OUTSIDE ControlsOverlay's visual tree
        //
        // Fading ControlsOverlay.Opacity only fades what lives under it. Two
        // pieces of keyboard-focus feedback do not:
        //   - the focus rectangle of the sliders (FocusVisualStyle) is an Adorner,
        //     drawn by the AdornerLayer that sits ABOVE the overlay content in the
        //     host's AdornerDecorator, so it ignores the overlay's Opacity;
        //   - ToolTips (the label shown for the focused control) are separate
        //     top-level popup windows owned by the control.
        // Both are made to follow the same fade, driven by the same
        // SetOverlayVisible call, so the existing fade lifecycle stays the only one.
        // =====================================================================

        private readonly List<ToolTip> _overlayToolTips = new List<ToolTip>();

        // Gives every overlay control an explicit, closable ToolTip instance
        // (a plain string ToolTip creates an internal one that cannot be closed).
        private void RegisterOverlayToolTips()
        {
            FrameworkElement[] owners =
            {
                CloseButton, BottomPlayPauseButton, Back10Button, Forward10Button,
                PreviousEpisodeButton, NextEpisodeButton, MuteButton, VolumeSlider,
                SeekSlider, AudioButton, SubtitleButton, SettingsButton, FullscreenButton
            };

            foreach (FrameworkElement owner in owners)
            {
                if (owner == null || owner.ToolTip == null) continue;

                ToolTip tip = owner.ToolTip as ToolTip;
                if (tip == null)
                {
                    tip = new ToolTip { Content = owner.ToolTip };
                    owner.ToolTip = tip;
                }

                _overlayToolTips.Add(tip);
            }
        }

        private static void SetOverlayToolTip(FrameworkElement owner, string text)
        {
            ToolTip tip = owner.ToolTip as ToolTip;
            if (tip != null)
                tip.Content = text;
            else
                owner.ToolTip = text;
        }

        private void FadeOverlayDecorations(bool visible, DoubleAnimation animation)
        {
            try
            {
                // No new tooltips while faded; restored when the controls return.
                ToolTipService.SetIsEnabled(ControlsOverlay, visible);

                if (!visible)
                {
                    foreach (ToolTip tip in _overlayToolTips)
                        tip.IsOpen = false;
                }

                System.Windows.Documents.AdornerLayer adorners =
                    System.Windows.Documents.AdornerLayer.GetAdornerLayer(ControlsOverlay);
                if (adorners != null)
                    adorners.BeginAnimation(UIElement.OpacityProperty, animation);
            }
            catch { }
        }

        private void HideControlsTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed) return;

            PollCursorActivity();

            PlaybackState state = _session != null ? _session.State : null;

            bool playbackRunning =
                state != null && state.IsPlaybackEstablished && !state.ReachedEndOfFile;
            bool paused = state == null || !state.IsPlaying;

            bool menuOpen = _uiState.OpenMenu != PlayerMenu.None;
            bool seeking = _uiState.IsSeekBarBeingDragged;

            bool visible = _overlayPolicy.ShouldBeVisible(
                DateTime.UtcNow,
                playbackRunning,
                paused,
                menuOpen,
                seeking);

            LogFadeInputsIfChanged(state, playbackRunning, paused, menuOpen, seeking, visible);

            SetOverlayVisible(visible, "tick");
        }

        // =====================================================================
        // [PLAYER-FADE] DIAGNOSTICS (logging only)
        //
        // Every show/hide of the controls is logged with the policy inputs and
        // WHO caused it: ShowControls/RestartHideTimer record their caller
        // (compiler-supplied method name), the cursor poll records the cursor
        // position and delta, and any change of the policy inputs (playing/
        // paused/established/eof/menu/seeking) is logged on its own. This
        // identifies the source of an unexpected fade cycle without altering the
        // fade behaviour.
        // =====================================================================

        private string _lastActivityReason = "start";
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private string _lastCursorDetail = "none";
        private string _lastFadeInputs;
        private int _fadeLogCount;
        private const int FadeLogLimit = 400;

        private void NoteOverlayActivity(string reason)
        {
            _overlayPolicy.NoteActivity(DateTime.UtcNow);
            _lastActivityReason = reason;
            _lastActivityUtc = DateTime.UtcNow;
        }

        private void FadeLog(string text)
        {
            if (_fadeLogCount >= FadeLogLimit) return;
            _fadeLogCount++;
            StremioVideoCenterLogger.Info("[PLAYER-FADE] " + text);
        }

        private void LogFadeTransition(bool visible, string trigger)
        {
            try
            {
                PlaybackState state = _session != null ? _session.State : null;
                FadeLog(string.Format(
                    "{0} t={1:HH:mm:ss.fff} trigger={2} lastActivity={3} ageMs={4:0} cursor={5} " +
                    "established={6} playing={7} eof={8} menu={9} seeking={10}",
                    visible ? "SHOW" : "HIDE", DateTime.Now, trigger ?? "?",
                    _lastActivityReason, (DateTime.UtcNow - _lastActivityUtc).TotalMilliseconds,
                    _lastCursorDetail,
                    state != null && state.IsPlaybackEstablished,
                    state != null && state.IsPlaying,
                    state != null && state.ReachedEndOfFile,
                    _uiState.OpenMenu, _uiState.IsSeekBarBeingDragged));
            }
            catch { }
        }

        private void LogFadeInputsIfChanged(
            PlaybackState state, bool running, bool paused, bool menuOpen, bool seeking, bool visible)
        {
            string inputs = string.Format(
                "running={0} paused={1} menu={2} seeking={3} established={4} playing={5} eof={6}",
                running, paused, menuOpen, seeking,
                state != null && state.IsPlaybackEstablished,
                state != null && state.IsPlaying,
                state != null && state.ReachedEndOfFile);

            if (inputs == _lastFadeInputs) return;

            _lastFadeInputs = inputs;
            FadeLog(string.Format("INPUTS t={0:HH:mm:ss.fff} {1} policyVisible={2}", DateTime.Now, inputs, visible));
        }

        // Samples the OS cursor: WPF never sees mouse movement over the native
        // mpv window, so this is what wakes the overlay when the mouse moves
        // over the video.
        private void PollCursorActivity()
        {
            NativePoint point;
            if (!GetCursorPos(out point)) return;

            bool moved = _haveCursorSample && (point.X != _lastCursorX || point.Y != _lastCursorY);
            int deltaX = _haveCursorSample ? point.X - _lastCursorX : 0;
            int deltaY = _haveCursorSample ? point.Y - _lastCursorY : 0;

            _haveCursorSample = true;
            _lastCursorX = point.X;
            _lastCursorY = point.Y;

            if (!moved) return;

            bool overPlayer = IsScreenPointOverPlayer(point);
            _lastCursorDetail = string.Format("({0},{1}) d=({2},{3}) overPlayer={4}", point.X, point.Y, deltaX, deltaY, overPlayer);

            // Cursor activity that arrives while the controls are hidden is the
            // case worth seeing; while visible it would only flood the log.
            if (!_overlayVisible)
                FadeLog(string.Format("CURSOR-MOVE-WHILE-HIDDEN t={0:HH:mm:ss.fff} {1}", DateTime.Now, _lastCursorDetail));

            if (overPlayer)
                ShowControls("PollCursorActivity");
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
