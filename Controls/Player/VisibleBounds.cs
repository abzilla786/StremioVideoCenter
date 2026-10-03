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

    // The overlay's content must never be laid out larger than the native
    // window that actually displays it: anything beyond the window's client
    // area is simply not painted, so a bottom-anchored control bar placed at the
    // bottom of an over-tall layout would be invisible although it exists, has
    // focus and shows tooltips.
    public static class OverlayFit
    {
        // desired: the layout size wanted (DIPs); windowClient: the popup
        // window's real client size in the same unit (<= 0 = unknown). Returns
        // the size to lay out with - never larger than the window.
        public static double Constrain(double desired, double windowClient)
        {
            if (double.IsNaN(desired) || double.IsInfinity(desired) || desired <= 0)
                return desired;

            if (double.IsNaN(windowClient) || double.IsInfinity(windowClient) || windowClient <= 0)
                return desired;

            return windowClient < desired - 1 ? windowClient : desired;
        }
    }

    // WPF limits a Popup's native window to a fraction of the screen, so the
    // controls popup can come out smaller than requested. When the popup window
    // is about to be sized (WM_WINDOWPOSCHANGING) this decides the size to use
    // instead: the size we asked for, in device pixels. It only ever ENLARGES a
    // window WPF made too small - never shrinks one - and ignores invalid input.
    public static class PopupSizeOverride
    {
        // wpfWidth/wpfHeight : the size WPF is about to apply (device pixels).
        // wantedDipWidth/Height : the size requested for the popup (DIPs).
        // scaleX/scaleY : the popup window's DIP -> device pixel scale.
        public static bool TryCompute(
            int wpfWidth, int wpfHeight,
            double wantedDipWidth, double wantedDipHeight,
            double scaleX, double scaleY,
            out int width, out int height)
        {
            width = wpfWidth;
            height = wpfHeight;

            if (!IsPositive(wantedDipWidth) || !IsPositive(wantedDipHeight) || !IsPositive(scaleX) || !IsPositive(scaleY))
                return false;

            int wantedWidth = (int)Math.Round(wantedDipWidth * scaleX);
            int wantedHeight = (int)Math.Round(wantedDipHeight * scaleY);

            bool changed = false;

            if (wpfWidth > 0 && wantedWidth > wpfWidth)
            {
                width = wantedWidth;
                changed = true;
            }

            if (wpfHeight > 0 && wantedHeight > wpfHeight)
            {
                height = wantedHeight;
                changed = true;
            }

            return changed;
        }

        private static bool IsPositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
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
