using StremioVideoCenterControls.Player;
using System;
using System.Runtime.InteropServices;

namespace StremioVideoCenter.Services
{
    // Physical-pixel window geometry for placing the Video Center window
    // exactly over Playnite's CLIENT area.
    //
    // Everything here is in physical screen pixels - the unit Win32 uses for
    // both reading Playnite's client rectangle and positioning our window - so
    // no device-independent-unit round trip (whose scale depends on which
    // monitor's DPI the window happens to be on) is involved. The client
    // rectangle (GetClientRect/ClientToScreen), not WPF's outer
    // ActualWidth/ActualHeight, is what excludes any title bar and borders.
    public static class HostWindowGeometry
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // The window's client area on screen, in physical pixels; null if the
        // handle is invalid or the window has no client area (e.g. minimised).
        public static PixelRect? GetClientRectOnScreen(IntPtr hwnd)
        {
            Rect client;
            Point origin = new Point { X = 0, Y = 0 };

            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out client) || !ClientToScreen(hwnd, ref origin))
                return null;

            PixelRect rect = new PixelRect(
                origin.X, origin.Y,
                origin.X + (client.Right - client.Left),
                origin.Y + (client.Bottom - client.Top));

            return rect.IsEmpty ? (PixelRect?)null : rect;
        }

        // Slice 21 (Playnite desktop-mode fix): a MAXIMISED Playnite desktop
        // window reports a client rectangle that extends past its monitor
        // (measured: about 8 px on every side, because the custom-chrome
        // window's invisible resize border is part of the client area; the
        // visible Playnite content is limited to the monitor's work area).
        // When the host is maximised AND its client rectangle is not fully
        // inside its monitor, the target is limited to that monitor's work
        // area (physical pixels, absolute virtual-screen coordinates - so
        // monitors that do not start at (0,0) are handled). In every other
        // case the client rectangle is returned unchanged: a windowed host,
        // or Playnite fullscreen mode, whose client rectangle is exactly the
        // monitor bounds and therefore already contained in them.
        public static PixelRect ClampMaximisedClientToMonitor(
            IntPtr hostHwnd, PixelRect client, bool hostIsMaximised)
        {
            if (!hostIsMaximised)
                return client;

            PixelRect monitor, work;
            if (!TryGetMonitorRects(hostHwnd, out monitor, out work))
                return client;

            if (monitor.Contains(client))
                return client;

            PixelRect clamped = PixelRect.Intersect(client, work.IsEmpty ? monitor : work);

            return clamped.IsEmpty ? client : clamped;
        }

        public static PixelRect? GetWindowRectOnScreen(IntPtr hwnd)
        {
            Rect rect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rect))
                return null;

            return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        public static bool SetWindowRect(IntPtr hwnd, PixelRect rect)
        {
            return hwnd != IntPtr.Zero &&
                   SetWindowPos(
                       hwnd, IntPtr.Zero,
                       (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
                       (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
                       SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // =====================================================================
        // MONITOR GEOMETRY
        // =====================================================================
        //
        // Read-only helpers (used by ClampMaximisedClientToMonitor). They never
        // move, resize or restyle any window. All rectangles are physical
        // screen pixels.

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        // Monitor bounds and work area (physical pixels) of the monitor
        // nearest the window.
        public static bool TryGetMonitorRects(IntPtr hwnd, out PixelRect monitor, out PixelRect work)
        {
            monitor = new PixelRect(0, 0, 0, 0);
            work = new PixelRect(0, 0, 0, 0);

            if (hwnd == IntPtr.Zero)
                return false;

            IntPtr handle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (handle == IntPtr.Zero)
                return false;

            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));

            if (!GetMonitorInfo(handle, ref info))
                return false;

            monitor = new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
            work = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            return true;
        }

        // True when the window is not already at the target rectangle (so a
        // re-sync does nothing - and cannot loop - once it is correct).
        public static bool NeedsUpdate(PixelRect current, PixelRect target)
        {
            return Math.Abs(current.Left - target.Left) >= 1 ||
                   Math.Abs(current.Top - target.Top) >= 1 ||
                   Math.Abs(current.Width - target.Width) >= 1 ||
                   Math.Abs(current.Height - target.Height) >= 1;
        }
    }
}
