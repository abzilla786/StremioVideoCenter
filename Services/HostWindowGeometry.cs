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
