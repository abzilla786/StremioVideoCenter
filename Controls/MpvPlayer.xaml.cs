using Playnite.SDK;
using Playnite.SDK.Events;
using StremioVideoCenter.Services;
using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

namespace StremioVideoCenter.Controls
{
    public partial class MpvPlayer : UserControl
    {
        private MpvHost _mpvHost;
        private MpvProcessService _mpv;
        private bool _started;
        private bool _disposed;

        public event EventHandler CloseRequested;

        public MpvPlayer()
        {
            InitializeComponent();

            Loaded += MpvPlayer_Loaded;
            Unloaded += MpvPlayer_Unloaded;
            SizeChanged += MpvPlayer_SizeChanged;
            PreviewKeyDown += MpvPlayer_PreviewKeyDown;
        }

        private async void MpvPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || _started)
                return;

            try
            {
                await EnsureStartedAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Unable to start MPV playback engine.\n\n" + ex.Message,
                    "MPV Playback Error");
            }
        }

        private void MpvPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            // Do not dispose here. PlaybackTestHost temporarily changes visibility
            // while the player is being managed by the Video Center.
        }

        private void MpvPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_mpvHost == null)
                return;

            try
            {
                _mpvHost.SynchronizeNativeWindow();
            }
            catch
            {
                // Size synchronization is best-effort while WPF is laying out.
            }
        }

        private async Task EnsureStartedAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvPlayer));

            // Only reuse the player when the underlying process is genuinely alive.
            if (_started &&
                _mpv != null &&
                _mpv.IsRunning)
            {
                return;
            }

            // Clean up a stale process/service before starting a new one.
            if (_mpv != null)
            {
                try { _mpv.Dispose(); } catch { }
                _mpv = null;
            }

            _started = false;

            if (_mpvHost == null)
            {
                _mpvHost = new MpvHost();
                VideoHost.Children.Add(_mpvHost);
            }

            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Render);

            if (_mpvHost.WindowHandle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "MPV video host window could not be created.");

            _mpv = new MpvProcessService();

            await _mpv.StartAsync(
                _mpvHost.WindowHandle);

            if (!_mpv.IsRunning)
            {
                throw new InvalidOperationException(
                    "MPV stopped immediately after startup.");
            }

            // mpv normally creates its own video window under --wid. Wait for
            // that native window, then verify its ownership. We only re-parent
            // if Windows reports that mpv did not attach it to our host already.
            await _mpvHost.WaitForAndAttachMpvWindowAsync(
                _mpv.ProcessId,
                3000);

            _started = true;

            // mpv creates its own child HWND under the --wid host. Make sure both
            // the WPF host and mpv's native child are using the real playback size.
            _mpvHost.SynchronizeNativeWindow();
            await Dispatcher.InvokeAsync(
                () => _mpvHost.SynchronizeNativeWindow(),
                DispatcherPriority.Render);
        }

        public async Task PlayVideoAsync(string url)
        {
            if (_disposed)
                return;

            await EnsureStartedAsync();

            if (_disposed || _mpv == null || _mpvHost == null)
                return;

            try
            {
                await _mpv.PlayAsync(url);

                if (_disposed || _mpv == null || _mpvHost == null)
                    return;

                // The child window can be recreated by mpv when a new file is
                // loaded. Verify the current session's HWND after loadfile,
                // but do not blindly re-parent a window that mpv already owns.
                await _mpvHost.WaitForAndAttachMpvWindowAsync(
                    _mpv.ProcessId,
                    3000);
            }
            catch (InvalidOperationException ex)
            {
                // If mpv died between startup and loadfile, restart this
                // playback session once. The player/host remains the same;
                // only the dead mpv process and IPC session are replaced.
                if (_disposed || _mpv == null ||
                    !ex.Message.Contains("mpv is not running"))
                    throw;

                try { _mpv.Dispose(); } catch { }
                _mpv = null;
                _started = false;

                await EnsureStartedAsync();

                if (_disposed || _mpv == null || _mpvHost == null)
                    return;

                await _mpv.PlayAsync(url);

                if (_disposed || _mpv == null || _mpvHost == null)
                    return;

                await _mpvHost.WaitForAndAttachMpvWindowAsync(
                    _mpv.ProcessId,
                    3000);
            }
        }

        public async Task PauseAsync()
        {
            if (_mpv == null)
                return;

            await _mpv.PauseAsync();
        }

        public async Task SeekRelativeAsync(double seconds)
        {
            if (_mpv == null)
                return;

            await _mpv.SeekRelativeAsync(seconds);
        }

        public async Task SetVolumeAsync(double volume)
        {
            if (_mpv == null)
                return;

            await _mpv.SetVolumeAsync(volume);
        }

        public async Task ToggleMuteAsync()
        {
            if (_mpv == null)
                return;

            await _mpv.ToggleMuteAsync();
        }

        public bool HandleControllerInput(ControllerInput button)
        {
            if (_disposed || _mpv == null)
                return false;

            // Stage 1/2: basic transport proof only.
            // Full Netflix-style focus navigation will be added after playback
            // is confirmed working with the embedded MPV surface.
            if (button == ControllerInput.B)
            {
                RaiseCloseRequested();
                return true;
            }

            return false;
        }

        private async void MpvPlayer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Escape || e.Key == Key.Back)
                {
                    RaiseCloseRequested();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Space || e.Key == Key.Enter)
                {
                    await PauseAsync();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Left)
                {
                    await SeekRelativeAsync(-10);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Right)
                {
                    await SeekRelativeAsync(10);
                    e.Handled = true;
                }
            }
            catch
            {
                // Input is best-effort during the playback proof stage.
            }
        }

        private void RaiseCloseRequested()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        public void DisposePlayer()
        {
            if (_disposed)
                return;

            _disposed = true;

            try { PreviewKeyDown -= MpvPlayer_PreviewKeyDown; } catch { }
            try { SizeChanged -= MpvPlayer_SizeChanged; } catch { }
            try { Unloaded -= MpvPlayer_Unloaded; } catch { }
            try { Loaded -= MpvPlayer_Loaded; } catch { }

            // MpvProcessService.Dispose() waits for mpv.exe to exit (and
            // force-kills it if required) before returning. Only after that do
            // we destroy the HwndHost, preventing the old native video HWND
            // from racing the next playback session.
            try { _mpv?.Dispose(); } catch { }
            _mpv = null;
            _started = false;

            try
            {
                if (_mpvHost != null)
                {
                    _mpvHost.WaitForMpvWindowsToDisappear(1000);
                    VideoHost.Children.Remove(_mpvHost);
                }
            }
            catch { }

            _mpvHost = null;
        }

        private sealed class MpvHost : HwndHost
        {
            private IntPtr _handle;

            public IntPtr WindowHandle
            {
                get { return _handle; }
            }

            protected override HandleRef BuildWindowCore(HandleRef hwndParent)
            {
                _handle = NativeMethods.CreateWindowEx(
                    0,
                    "STATIC",
                    string.Empty,
                    NativeMethods.WS_CHILD |
                    NativeMethods.WS_VISIBLE |
                    NativeMethods.WS_CLIPCHILDREN |
                    NativeMethods.WS_CLIPSIBLINGS,
                    0,
                    0,
                    1,
                    1,
                    hwndParent.Handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (_handle == IntPtr.Zero)
                    throw new InvalidOperationException("Unable to create the MPV host window.");

                return new HandleRef(this, _handle);
            }

            protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
            {
                base.OnRenderSizeChanged(sizeInfo);
                SynchronizeNativeWindow();
            }

            public async Task<bool> WaitForAndAttachMpvWindowAsync(
                int mpvProcessId,
                int timeoutMilliseconds)
            {
                if (_handle == IntPtr.Zero || mpvProcessId <= 0)
                    return false;

                int elapsed = 0;

                while (elapsed < timeoutMilliseconds)
                {
                    IntPtr mpvWindow = FindMpvChildWindow(mpvProcessId);

                    if (mpvWindow != IntPtr.Zero)
                    {
                        AttachMpvChildWindow(mpvWindow);
                        SynchronizeNativeWindow();
                        return true;
                    }

                    await Task.Delay(50).ConfigureAwait(true);
                    elapsed += 50;
                }

                return false;
            }

            private IntPtr FindMpvChildWindow(int mpvProcessId)
            {
                IntPtr found = IntPtr.Zero;

                NativeMethods.EnumChildWindows(
                    _handle,
                    delegate(IntPtr child, IntPtr parameter)
                    {
                        int processId = 0;
                        NativeMethods.GetWindowThreadProcessId(
                            child,
                            out processId);

                        if (processId == mpvProcessId)
                        {
                            found = child;
                            return false;
                        }

                        return true;
                    },
                    IntPtr.Zero);

                return found;
            }

            private void AttachMpvChildWindow(IntPtr child)
            {
                if (child == IntPtr.Zero || _handle == IntPtr.Zero)
                    return;

                IntPtr currentParent = NativeMethods.GetParent(child);

                // In the normal --wid path mpv has already made the supplied
                // HWND the parent. Avoid touching styles or calling SetParent
                // again because doing so can destabilise the native window
                // across repeated playback sessions.
                if (currentParent != _handle)
                {
                    IntPtr style = NativeMethods.GetWindowLongPtr(
                        child,
                        NativeMethods.GWL_STYLE);

                    long styleValue = style.ToInt64();
                    styleValue &= ~((long)NativeMethods.WS_POPUP);
                    styleValue |= NativeMethods.WS_CHILD |
                                  NativeMethods.WS_CLIPSIBLINGS |
                                  NativeMethods.WS_CLIPCHILDREN;

                    NativeMethods.SetWindowLongPtr(
                        child,
                        NativeMethods.GWL_STYLE,
                        new IntPtr(styleValue));

                    NativeMethods.SetParent(child, _handle);
                }

                NativeMethods.ShowWindow(child, NativeMethods.SW_SHOW);
                NativeMethods.BringWindowToTop(child);
            }

            public void WaitForMpvWindowsToDisappear(int timeoutMilliseconds)
            {
                if (_handle == IntPtr.Zero)
                    return;

                int elapsed = 0;
                while (elapsed < timeoutMilliseconds)
                {
                    bool mpvChildExists = false;

                    NativeMethods.EnumChildWindows(
                        _handle,
                        delegate(IntPtr child, IntPtr parameter)
                        {
                            int processId = 0;
                            NativeMethods.GetWindowThreadProcessId(
                                child,
                                out processId);

                            // At shutdown the process should already be gone,
                            // so any remaining child is safe to treat as stale.
                            mpvChildExists = true;
                            return false;
                        },
                        IntPtr.Zero);

                    if (!mpvChildExists)
                        return;

                    System.Threading.Thread.Sleep(25);
                    elapsed += 25;
                }
            }

            public void SynchronizeNativeWindow()
            {
                if (_handle == IntPtr.Zero)
                    return;

                int width = Math.Max(1, (int)Math.Round(ActualWidth));
                int height = Math.Max(1, (int)Math.Round(ActualHeight));

                NativeMethods.SetWindowPos(
                    _handle,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_NOZORDER |
                    NativeMethods.SWP_SHOWWINDOW);

                // Size the host first. The mpv child is explicitly attached
                // and normalised after startup/playback, so there is no need
                // to resize arbitrary child HWNDs here.
                NativeMethods.EnumChildWindows(
                    _handle,
                    delegate(IntPtr child, IntPtr parameter)
                    {
                        NativeMethods.SetWindowPos(
                            child,
                            IntPtr.Zero,
                            0,
                            0,
                            width,
                            height,
                            NativeMethods.SWP_NOACTIVATE |
                            NativeMethods.SWP_NOZORDER |
                            NativeMethods.SWP_SHOWWINDOW);

                        return true;
                    },
                    IntPtr.Zero);
            }

            protected override void DestroyWindowCore(HandleRef hwnd)
            {
                if (hwnd.Handle != IntPtr.Zero)
                    NativeMethods.DestroyWindow(hwnd.Handle);

                _handle = IntPtr.Zero;
            }
        }

        private static class NativeMethods
        {
            public const int WS_CHILD = 0x40000000;
            public const int WS_VISIBLE = 0x10000000;
            public const int WS_CLIPSIBLINGS = 0x04000000;
            public const int WS_CLIPCHILDREN = 0x02000000;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_SHOWWINDOW = 0x0040;

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateWindowEx(
                int exStyle,
                string className,
                string windowName,
                int style,
                int x,
                int y,
                int width,
                int height,
                IntPtr parent,
                IntPtr menu,
                IntPtr instance,
                IntPtr param);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyWindow(IntPtr handle);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(
                IntPtr hWnd,
                IntPtr hWndInsertAfter,
                int x,
                int y,
                int cx,
                int cy,
                uint uFlags);

            public const int GWL_STYLE = -16;
            public const long WS_POPUP = unchecked((long)0x80000000);
            public const int SW_SHOW = 5;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetParent(
                IntPtr child,
                IntPtr newParent);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetParent(
                IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern uint GetWindowThreadProcessId(
                IntPtr hWnd,
                out int processId);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetWindowLongPtr(
                IntPtr hWnd,
                int nIndex);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowLongPtr(
                IntPtr hWnd,
                int nIndex,
                IntPtr value);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindow(
                IntPtr hWnd,
                int nCmdShow);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool BringWindowToTop(
                IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumChildWindows(
                IntPtr hWndParent,
                EnumChildProc lpEnumFunc,
                IntPtr lParam);

            public delegate bool EnumChildProc(
                IntPtr hWnd,
                IntPtr lParam);
        }
    }
}
