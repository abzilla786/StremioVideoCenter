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

    // Result of HeroLayout.Compute: the uniform scale applied to the hero's
    // design layout, and the resulting on-screen sizes (WPF DIPs).
    public struct HeroLayoutResult
    {
        public double Scale;
        public double Width;       // hero media frame width
        public double Height;      // hero media frame height (aspect preserved)
        public double ContentTop;  // where the info panel starts: the frame height
        public double ContentWidth;// info panel content width (= frame width)
    }

    // Responsive sizing of the Home hero. 1240 x 698 is the hero's DESIGN
    // REFERENCE, not a runtime size: the hero (media frame, arrows, info text)
    // is laid out at that size and scaled UNIFORMLY, so its aspect ratio and the
    // relationships between its parts never change.
    //
    // The scale comes only from the available viewport (WPF DIPs, as given by
    // the layout - no display, device or resolution is ever named):
    //   - by height: the hero keeps the share of the viewport height the
    //     original design had (a 698 frame in a 1440-high viewport), which
    //     leaves room for the navigation bar and the first content rows;
    //   - by width: it never uses more than WidthFraction of the viewport width,
    //     so the carousel arrows at its edges stay inside the viewport.
    // The smaller of the two wins, bounded by MinScale / MaxScale.
    public static class HeroLayout
    {
        public const double DesignWidth = 1240;
        public const double DesignHeight = 698;

        // The viewport height the hero's height proportion was designed against.
        public const double ReferenceViewportHeight = 1440;

        // Largest share of the viewport width the hero may use.
        public const double WidthFraction = 0.96;

        public const double MinScale = 0.4;
        public const double MaxScale = 2.0;

        // Carousel arrow geometry in design units (they scale with the hero):
        // they sit ArrowInset inside the frame edge, never flush against it.
        public const double ArrowWidth = 68;
        public const double ArrowInset = 18;

        private static bool IsUsable(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static HeroLayoutResult Compute(double viewportWidth, double viewportHeight)
        {
            double scale = double.MaxValue;

            if (IsUsable(viewportHeight))
                scale = Math.Min(scale, viewportHeight / ReferenceViewportHeight);

            if (IsUsable(viewportWidth))
                scale = Math.Min(scale, WidthFraction * viewportWidth / DesignWidth);

            // Viewport unknown: show the design size.
            if (scale == double.MaxValue)
                scale = 1.0;

            scale = Math.Max(MinScale, Math.Min(MaxScale, scale));

            double width = Math.Round(DesignWidth * scale);

            HeroLayoutResult result = new HeroLayoutResult();
            result.Scale = scale;
            result.Width = width;
            result.Height = Math.Round(width * DesignHeight / DesignWidth);
            result.ContentTop = result.Height;
            result.ContentWidth = width;
            return result;
        }
    }
}
