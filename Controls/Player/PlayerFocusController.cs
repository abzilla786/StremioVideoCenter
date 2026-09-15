using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StremioVideoCenterControls.Player
{
    // Implemented by any focusable control (sliders) that wants to consume
    // Left/Right itself instead of yielding to row navigation. See PlayerSlider.
    public interface IDirectionalInputConsumer
    {
        bool HandleLeft();
        bool HandleRight();
    }

    // One horizontal row of focusable elements (or, when used as a pushed menu
    // scope, one vertical list of menu items).
    public sealed class FocusGroup
    {
        private readonly List<UIElement> _elements = new List<UIElement>();

        public FocusGroup(params UIElement[] elements)
        {
            if (elements != null)
                _elements.AddRange(elements.Where(e => e != null));
        }

        public IReadOnlyList<UIElement> Elements
        {
            get { return _elements; }
        }

        public void Add(UIElement element)
        {
            if (element != null && !_elements.Contains(element))
                _elements.Add(element);
        }
    }

    // A set of rows, navigated with Up/Down between rows and Left/Right within
    // the current row.
    public sealed class FocusGrid
    {
        private readonly List<FocusGroup> _rows = new List<FocusGroup>();

        public IReadOnlyList<FocusGroup> Rows
        {
            get { return _rows; }
        }

        // The element focus lands on when the player opens, and the element
        // focus falls back to when a pushed menu scope is popped without an
        // explicit origin control recorded.
        public UIElement DefaultElement { get; set; }

        public void AddRow(FocusGroup row)
        {
            _rows.Add(row);
        }

        public FocusGroup FindRow(UIElement element)
        {
            if (element == null) return null;
            return _rows.FirstOrDefault(r => r.Elements.Contains(element));
        }

        public int IndexOfRow(FocusGroup row)
        {
            return _rows.IndexOf(row);
        }
    }

    // Manages controller/keyboard focus across the player's control bar and any
    // opened menu (Audio/Subtitle/Settings), as a scope stack:
    //   - The root scope is the always-present FocusGrid (Close / Center / Bottom rows).
    //   - Opening a menu pushes a single FocusGroup (a flat list) as the active scope.
    //   - B pops the active scope. Popping the root scope means "close the
    //     player", signalled by PopScope returning null - the caller decides
    //     what "close" means (MpvPlayer raises CloseRequested).
    public sealed class PlayerFocusController
    {
        private readonly FocusGrid _rootGrid;
        private readonly Stack<object> _scopes = new Stack<object>();
        private readonly Stack<UIElement> _returnFocusStack = new Stack<UIElement>();

        public PlayerFocusController(FocusGrid rootGrid)
        {
            if (rootGrid == null)
                throw new ArgumentNullException(nameof(rootGrid));

            _rootGrid = rootGrid;
            _scopes.Push(rootGrid);
        }

        public bool IsAtRootScope
        {
            get { return _scopes.Count == 1; }
        }

        public void PushMenuScope(FocusGroup menuGroup, UIElement elementToReturnFocusTo)
        {
            if (menuGroup == null)
                return;

            _returnFocusStack.Push(elementToReturnFocusTo);
            _scopes.Push(menuGroup);

            if (menuGroup.Elements.Count > 0)
                Focus(menuGroup.Elements[0]);
        }

        // Pops the active scope and restores focus to whatever control opened
        // it. Returns null if the root scope was already active - the caller
        // should treat that as "close the player" rather than "close a menu".
        public UIElement PopScope()
        {
            if (_scopes.Count <= 1)
                return null;

            _scopes.Pop();

            UIElement returnTo = _returnFocusStack.Count > 0 ? _returnFocusStack.Pop() : null;
            if (returnTo == null)
                returnTo = _rootGrid.DefaultElement;

            if (returnTo != null)
                Focus(returnTo);

            return returnTo;
        }

        public void MoveHorizontal(int direction)
        {
            UIElement current = Keyboard.FocusedElement as UIElement;

            IDirectionalInputConsumer consumer = current as IDirectionalInputConsumer;
            if (consumer != null)
            {
                bool handled = direction < 0 ? consumer.HandleLeft() : consumer.HandleRight();
                if (handled)
                    return;
            }

            FocusGroup group = CurrentGroup(current);
            MoveWithinGroup(group, current, direction);
        }

        public void MoveVertical(int direction)
        {
            object scope = _scopes.Peek();

            FocusGrid grid = scope as FocusGrid;
            if (grid == null)
            {
                // A menu scope is a flat list; Up/Down moves the list selection
                // the same way Left/Right would move focus within a row.
                FocusGroup menuGroup = scope as FocusGroup;
                MoveWithinGroup(menuGroup, Keyboard.FocusedElement as UIElement, direction);
                return;
            }

            UIElement current = Keyboard.FocusedElement as UIElement;
            FocusGroup currentRow = grid.FindRow(current);
            int rowIndex = currentRow != null ? grid.IndexOfRow(currentRow) : 0;

            int nextIndex = rowIndex + direction;
            if (nextIndex < 0 || nextIndex >= grid.Rows.Count)
                return;

            FocusGroup nextRow = grid.Rows[nextIndex];
            if (nextRow.Elements.Count > 0)
                Focus(nextRow.Elements[0]);
        }

        public void FocusRootDefault()
        {
            if (_rootGrid.DefaultElement != null)
                Focus(_rootGrid.DefaultElement);
        }

        private FocusGroup CurrentGroup(UIElement current)
        {
            object scope = _scopes.Peek();

            FocusGroup menuGroup = scope as FocusGroup;
            if (menuGroup != null)
                return menuGroup;

            FocusGrid grid = scope as FocusGrid;
            if (grid == null)
                return null;

            return grid.FindRow(current) ?? grid.Rows.FirstOrDefault();
        }

        private void MoveWithinGroup(
            FocusGroup group,
            UIElement current,
            int direction)
        {
            if (group == null || group.Elements.Count == 0)
                return;

            int index = -1;

            for (int i = 0; i < group.Elements.Count; i++)
            {
                if (ReferenceEquals(group.Elements[i], current))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                index = 0;

            index += direction;
            if (index < 0) index = group.Elements.Count - 1;
            if (index >= group.Elements.Count) index = 0;

            Focus(group.Elements[index]);
        }

        public void Focus(UIElement element)
        {
            if (element == null)
                return;

            Control control = element as Control;
            if (control != null)
                control.Focus();
            else
                element.Focus();

            IInputElement inputElement = element as IInputElement;
            if (inputElement != null)
                Keyboard.Focus(inputElement);
        }
    }
}
