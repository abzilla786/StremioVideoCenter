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
    //   - Slice 21 (drag arbitration): a vertical page and the horizontal
    //     rows inside it all see the same press, so they share ONE gesture
    //     record instead of each judging the movement on its own. The
    //     gesture remembers where the press began. The first time the
    //     pointer has travelled StartThreshold from there, the dominant
    //     direction is decided ONCE (horizontal when |dx| >= |dy|), and only
    //     the viewer scrolling that axis may claim the gesture. The claim is
    //     final: the other viewers ignore the rest of the gesture, so a
    //     slight movement across the drag axis can no longer hand the drag
    //     to the parent page (or back) midway.
    //   - Once claimed, the owning viewer captures the mouse. The element
    //     that was pressed loses its capture, so its click never fires on
    //     release, and the release itself is swallowed.
    //   - Capture is released on mouse-up, on loss of capture, when the
    //     button is found not pressed on a later move, and when the viewer
    //     unloads - it can never stay stuck, and the gesture is ended with
    //     it.
    //   - Presses on text boxes, sliders, scroll bars, combo boxes and
    //     other controls where dragging already means something are
    //     ignored.
    //   - The mouse wheel is not handled here at all.
    public static class DragScroll
    {
        // Pixels the pointer must travel before a press becomes a drag.
        // MediaCard uses the same distance to tell a click from a drag.
        private const double StartThreshold = 8.0;

        private static readonly ConditionalWeakTable<ScrollViewer, DragState> States =
            new ConditionalWeakTable<ScrollViewer, DragState>();

        // The one press currently being tracked. Every attached viewer on
        // the press's route reads and updates this same record. Replaced by
        // the next press and cleared on release; UI thread only.
        private static Gesture _gesture;

        private sealed class Gesture
        {
            // The press event itself: the same instance is routed through
            // every viewer, which is how they recognise one shared press.
            public MouseButtonEventArgs Press;

            // Pointer position at press, relative to the window root, so it
            // does not move when a viewer scrolls underneath the pointer.
            public Point Origin;

            // The press began on a control where dragging means something
            // else (text box, slider, scroll bar...): nobody may drag.
            public bool Blocked;

            // The movement threshold has been crossed and Axis is final.
            public bool Decided;
            public DragScrollAxis Axis;

            // The single viewer that claimed the gesture, if any.
            public DragState Owner;

            // Cancelled or finished early (capture refused or lost, button
            // released unseen, viewer unloaded): nobody may drag any more.
            public bool Ended;
        }

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
            private bool _dragging;
            private Point _dragAnchor;
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

                // The first viewer on the route starts the shared gesture;
                // the others (same event instance) join it.
                Gesture gesture = _gesture;

                if (gesture == null || !ReferenceEquals(gesture.Press, e))
                {
                    gesture = new Gesture
                    {
                        Press = e,
                        Origin = e.GetPosition(null)
                    };

                    _gesture = gesture;
                }

                if (IsExcludedSource(e.OriginalSource as DependencyObject))
                {
                    gesture.Blocked = true;
                }
            }

            private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
            {
                // The button was released somewhere we did not see (another
                // window, capture taken elsewhere): never keep dragging, and
                // end the shared gesture so no other viewer starts one.
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndDrag();

                    Gesture released = _gesture;

                    if (released != null)
                    {
                        released.Ended = true;
                    }

                    return;
                }

                Gesture gesture = _gesture;

                if (gesture == null || gesture.Blocked || gesture.Ended)
                {
                    return;
                }

                // Another viewer owns this gesture: it is final, leave it be.
                if (gesture.Owner != null)
                {
                    if (ReferenceEquals(gesture.Owner, this))
                    {
                        ContinueDrag(e);
                    }

                    return;
                }

                if (!gesture.Decided)
                {
                    Point position = e.GetPosition(null);
                    double dx = position.X - gesture.Origin.X;
                    double dy = position.Y - gesture.Origin.Y;

                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < StartThreshold)
                    {
                        return;
                    }

                    // Decided once, here, and never revisited.
                    gesture.Decided = true;
                    gesture.Axis = Math.Abs(dx) >= Math.Abs(dy)
                        ? DragScrollAxis.Horizontal
                        : DragScrollAxis.Vertical;
                }

                // Only a viewer that scrolls the decided axis may claim it.
                if (gesture.Axis != Axis || !CanScroll)
                {
                    return;
                }

                gesture.Owner = this;
                _dragging = true;
                _dragAnchor = e.GetPosition(_viewer);
                _originOffset = CurrentOffset;

                _viewer.CaptureMouse();

                if (!_viewer.IsMouseCaptured)
                {
                    // Capture refused: do not drag without it, and do not let
                    // another viewer pick the gesture up half-way.
                    EndDrag();
                    gesture.Ended = true;
                    return;
                }

                ContinueDrag(e);
            }

            private void ContinueDrag(MouseEventArgs e)
            {
                if (!_dragging)
                {
                    return;
                }

                Point position = e.GetPosition(_viewer);

                double travelled = IsVertical ? position.Y - _dragAnchor.Y : position.X - _dragAnchor.X;
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

                // The press is over. (Runs on every viewer on the route; the
                // owner's own state is independent of this reference.)
                _gesture = null;

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

                if (_dragging)
                {
                    _dragging = false;
                    EndOwnedGesture();
                }
            }

            private void Viewer_Unloaded(object sender, RoutedEventArgs e)
            {
                EndDrag();
            }

            private void EndDrag()
            {
                bool hadCapture = _dragging;

                _dragging = false;

                if (hadCapture)
                {
                    EndOwnedGesture();

                    if (_viewer.IsMouseCaptured)
                    {
                        _viewer.ReleaseMouseCapture();
                    }
                }
            }

            // If this viewer owns the current gesture, that gesture is over.
            private void EndOwnedGesture()
            {
                Gesture gesture = _gesture;

                if (gesture != null && ReferenceEquals(gesture.Owner, this))
                {
                    gesture.Ended = true;
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
