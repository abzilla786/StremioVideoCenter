using System;

namespace StremioVideoCenterControls.Player
{
    // An axis-aligned rectangle in physical (device) pixels.
    public struct PixelRect
    {
        public double Left;
        public double Top;
        public double Right;
        public double Bottom;

        public PixelRect(double left, double top, double right, double bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public double Width { get { return Right - Left; } }
        public double Height { get { return Bottom - Top; } }
        public bool IsEmpty { get { return Width <= 0 || Height <= 0; } }

        public bool Contains(PixelRect other)
        {
            return Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
        }

        public static PixelRect Intersect(PixelRect a, PixelRect b)
        {
            return new PixelRect(
                Math.Max(a.Left, b.Left),
                Math.Max(a.Top, b.Top),
                Math.Min(a.Right, b.Right),
                Math.Min(a.Bottom, b.Bottom));
        }
    }

    // The part of the player surface that is actually visible on screen.
    //
    // The mpv player fills a WPF container whose nominal size can be larger
    // than what the user can see (the host window overshooting the screen or
    // Playnite's client area, for example). The controls overlay is a Popup
    // (it has to be, to render above the native mpv window), so it must be
    // sized to the VISIBLE part of the player - otherwise its bottom-anchored
    // control bar lands off-screen. Pure so it can be unit-checked.
    public static class VisibleBounds
    {
        // gridRect : the player container's nominal screen rectangle.
        // hostClientRect : the host window's client area (null if unknown).
        // monitorRect / workRect : the monitor containing the player; the work
        //   area excludes the taskbar.
        // The player is clipped to the host client area and to the monitor
        // (the work area, unless the player covers the whole monitor, in which
        // case nothing - such as a taskbar - is in front of it). If the result
        // would be empty the nominal rectangle is returned unchanged, so the
        // overlay can never collapse.
        public static PixelRect Compute(
            PixelRect gridRect,
            PixelRect? hostClientRect,
            PixelRect monitorRect,
            PixelRect workRect)
        {
            PixelRect visible = gridRect;

            if (hostClientRect.HasValue && !hostClientRect.Value.IsEmpty)
                visible = PixelRect.Intersect(visible, hostClientRect.Value);

            PixelRect screen = gridRect.Contains(monitorRect) ? monitorRect : workRect;
            if (screen.IsEmpty)
                screen = monitorRect;

            if (!screen.IsEmpty)
                visible = PixelRect.Intersect(visible, screen);

            return visible.IsEmpty ? gridRect : visible;
        }
    }
}
