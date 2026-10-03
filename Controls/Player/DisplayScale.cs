using System;

namespace StremioVideoCenterControls.Player
{
    // Unit conversion between WPF device-independent units (DIPs) and the
    // physical pixels Win32 uses (SetWindowPos, mpv's --wid window, ...). WPF
    // sizes such as ActualWidth are DIPs; passing them straight to a Win32
    // call is only right at exactly 100% display scaling.
    public static class DisplayScale
    {
        // dipSize * scale, rounded, never below 1 pixel. scale <= 0 or NaN is
        // treated as 1 (no conversion available).
        public static int ToPixels(double dipSize, double scale)
        {
            double factor = (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) ? 1.0 : scale;

            if (double.IsNaN(dipSize) || double.IsInfinity(dipSize))
                return 1;

            return Math.Max(1, (int)Math.Round(dipSize * factor));
        }
    }

    // The Home hero's media frame has a design size (1240 x 698). It must never
    // be larger than the space actually available, otherwise it overflows a
    // narrow viewport and is cut off, or dominates a short one. The size is
    // scaled DOWN uniformly (the design aspect ratio is kept, so artwork is not
    // distorted) and never scaled up past the design size.
    public static class HeroLayout
    {
        public const double DesignWidth = 1240;
        public const double DesignHeight = 698;

        // Fraction of the viewport height the hero may occupy, leaving the
        // navigation bar and the first rows visible.
        public const double MaxHeightFraction = 0.75;

        public const double MinWidth = 320;

        // The hero container's design minimum height (750) is the media frame
        // height plus a fixed margin; it scales with the frame.
        public const double DesignContainerMinHeight = 750;

        public static double ContainerMinHeight(double heroHeight)
        {
            return Math.Round(heroHeight * DesignContainerMinHeight / DesignHeight);
        }

        public static void Compute(
            double viewportWidth, double viewportHeight,
            out double width, out double height)
        {
            double scale = 1.0;

            if (viewportWidth > 0 && !double.IsInfinity(viewportWidth))
                scale = Math.Min(scale, viewportWidth / DesignWidth);

            if (viewportHeight > 0 && !double.IsInfinity(viewportHeight))
                scale = Math.Min(scale, (viewportHeight * MaxHeightFraction) / DesignHeight);

            width = Math.Max(MinWidth, Math.Round(DesignWidth * scale));
            height = Math.Round(width * DesignHeight / DesignWidth);
        }
    }
}
