using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace StremioVideoCenterControls
{
    public enum DragScrollAxis
    {
        Vertical,
        Horizontal
    }

    // =============================================================
    // MOUSE DRAG SCROLLING (Slice 20 Stage 3, workstream C)
    // =============================================================
    //
    // Press the left mouse button on a scrollable page (or a media row),
    // drag, and the content follows - the mouse equivalent of the touch
    // drag a phone user expects when Zino is streamed to a phone, where
    // touch arrives as ordinary mouse press/move/release. No touch APIs
    // are used.
    //
    // One attached helper per ScrollViewer: DragScroll.Attach(viewer, axis).
    //
    //   - Nothing is captured on press, so a plain click reaches the card or
    //     button exactly as before.
    //   - Dragging starts only after the pointer moves more than a small
    //     threshold AND mostly along this viewer's axis (so a vertical page
    //     and a horizontal row inside it never fight: whichever axis the
    //     user drags decides who scrolls).
    //   - Once dragging, the viewer captures the mouse. The element that was
    //     pressed loses its capture, so its click never fires on release,
    //     and the release itself is swallowed.
    //   - Capture is released on mouse-up, on loss of capture, when the
    //     button is found not pressed on a later move, and when the viewer
    //     unloads - it can never stay stuck.
    //   - Presses on text boxes, sliders, scroll bars, combo boxes and
    //     other controls where dragging already means something are
    //     ignored.
    public static class DragScroll
    {
        // Pixels the pointer must travel before a press becomes a drag.
        private const double StartThreshold = 8.0;

        private static readonly ConditionalWeakTable<ScrollViewer, DragState> States =
            new ConditionalWeakTable<ScrollViewer, DragState>();

        public static void Attach(ScrollViewer viewer, DragScrollAxis axis)
        {
            if (viewer == null)
            {
                return;
            }

            DragState existing;

            if (States.TryGetValue(viewer, out existing))
            {
                return;
            }

            DragState state = new DragState(viewer, axis);
            States.Add(viewer, state);
            state.Hook();
        }

        private static DependencyObject ParentOf(DependencyObject element)
        {
            if (element == null)
            {
                return null;
            }

            if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(element);
            }

            return LogicalTreeHelper.GetParent(element);
        }

        private sealed class DragState
        {
            private readonly ScrollViewer _viewer;
            private bool _pressed;
            private bool _dragging;
            private Point _origin;
            private double _originOffset;

            public DragState(ScrollViewer viewer, DragScrollAxis axis)
            {
                _viewer = viewer;
                Axis = axis;
            }

            public DragScrollAxis Axis { get; private set; }

            public void Hook()
            {
                // A viewer with no background only receives the mouse over
                // its visible content, so a drag that starts in a gap
                // between cards would be missed. A transparent background
                // is hit-testable and looks exactly the same.
                if (_viewer.Background == null)
                {
                    _viewer.Background = Brushes.Transparent;
                }

                _viewer.PreviewMouseLeftButtonDown += Viewer_PreviewMouseLeftButtonDown;
                _viewer.PreviewMouseMove += Viewer_PreviewMouseMove;
                _viewer.PreviewMouseLeftButtonUp += Viewer_PreviewMouseLeftButtonUp;
                _viewer.LostMouseCapture += Viewer_LostMouseCapture;
                _viewer.Unloaded += Viewer_Unloaded;
            }

            private bool IsVertical
            {
                get { return Axis == DragScrollAxis.Vertical; }
            }

            private bool CanScroll
            {
                get
                {
                    // Item-based (logical) scrolling has no pixel offsets to
                    // drag; every viewer here scrolls in pixels.
                    if (_viewer.CanContentScroll)
                    {
                        return false;
                    }

                    return IsVertical
                        ? _viewer.ExtentHeight > _viewer.ViewportHeight + 0.5
                        : _viewer.ExtentWidth > _viewer.ViewportWidth + 0.5;
                }
            }

            private double CurrentOffset
            {
                get { return IsVertical ? _viewer.VerticalOffset : _viewer.HorizontalOffset; }
            }

            private double MaxOffset
            {
                get
                {
                    return IsVertical
                        ? Math.Max(0, _viewer.ExtentHeight - _viewer.ViewportHeight)
                        : Math.Max(0, _viewer.ExtentWidth - _viewer.ViewportWidth);
                }
            }

            private void Viewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                // A new press always starts clean, whatever state a lost
                // event may have left behind.
                EndDrag();

                DependencyObject source = e.OriginalSource as DependencyObject;

                if (IsExcludedSource(source))
                {
                    return;
                }

                _pressed = true;
                _origin = e.GetPosition(_viewer);
            }

            private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
            {
                if (!_pressed && !_dragging)
                {
                    return;
                }

                // The button was released somewhere we did not see (another
                // window, capture taken elsewhere): never keep dragging.
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndDrag();
                    return;
                }

                Point position = e.GetPosition(_viewer);

                if (!_dragging)
                {
                    double dx = position.X - _origin.X;
                    double dy = position.Y - _origin.Y;
                    double along = Math.Abs(IsVertical ? dy : dx);
                    double across = Math.Abs(IsVertical ? dx : dy);

                    if (along < StartThreshold || along < across)
                    {
                        return;
                    }

                    if (!CanScroll)
                    {
                        return;
                    }

                    _dragging = true;
                    _origin = position;
                    _originOffset = CurrentOffset;

                    _viewer.CaptureMouse();

                    if (!_viewer.IsMouseCaptured)
                    {
                        // Capture refused: do not drag without it.
                        EndDrag();
                        return;
                    }
                }

                double travelled = IsVertical ? position.Y - _origin.Y : position.X - _origin.X;
                double target = Math.Max(0, Math.Min(MaxOffset, _originOffset - travelled));

                if (IsVertical)
                {
                    _viewer.ScrollToVerticalOffset(target);
                }
                else
                {
                    _viewer.ScrollToHorizontalOffset(target);
                }

                e.Handled = true;
            }

            private void Viewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                bool wasDragging = _dragging;

                EndDrag();

                if (wasDragging)
                {
                    // The release that ends a drag is not a click.
                    e.Handled = true;
                }
            }

            private void Viewer_LostMouseCapture(object sender, MouseEventArgs e)
            {
                // LostMouseCapture bubbles: the pressed button losing ITS
                // capture (which happens the moment this viewer takes over)
                // must not end the drag. Only this viewer losing capture does.
                if (!ReferenceEquals(e.OriginalSource, _viewer))
                {
                    return;
                }

                _pressed = false;
                _dragging = false;
            }

            private void Viewer_Unloaded(object sender, RoutedEventArgs e)
            {
                EndDrag();
            }

            private void EndDrag()
            {
                bool hadCapture = _dragging;

                _pressed = false;
                _dragging = false;

                if (hadCapture && _viewer.IsMouseCaptured)
                {
                    _viewer.ReleaseMouseCapture();
                }
            }

            // A press that should never start a drag: controls where a mouse
            // drag already has its own meaning.
            private bool IsExcludedSource(DependencyObject source)
            {
                DependencyObject current = source;

                while (current != null && !ReferenceEquals(current, _viewer))
                {
                    if (current is System.Windows.Controls.Primitives.TextBoxBase ||
                        current is PasswordBox ||
                        current is RangeBase ||
                        current is Thumb ||
                        current is ScrollBar ||
                        current is ComboBox)
                    {
                        return true;
                    }

                    current = ParentOf(current);
                }

                return false;
            }
        }
    }
}
