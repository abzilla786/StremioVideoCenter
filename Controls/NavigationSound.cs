using StremioVideoCenter.Services;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace StremioVideoCenterControls
{
    // =============================================================
    // NAVIGATION SOUND (consistent focus "tick" outside Playnite's list)
    // =============================================================
    //
    // The tick heard on the hero buttons, carousel and player controls is
    // Playnite Fullscreen's own navigation sound: its shell plays it when a
    // Button, ListBoxItem, CheckBox, Slider, ComboBox or TextBox receives
    // focus. This extension has no sound of its own. Two kinds of control
    // used here are not on that list and were therefore silent: MediaCard (a
    // UserControl) and the plain ToggleButton chips (genre / category /
    // media-type). This class gives exactly those two the same tick.
    //
    // Playnite's SDK exposes no sound API, so the sound is reused by calling
    // Playnite.FullscreenApp.FullscreenApplication.PlayNavigateSound - a
    // public static, parameterless method (verified against the installed
    // Playnite.FullscreenApp.exe) that also honours the user's Fullscreen
    // interface volume. The call is isolated here, resolved once, and fails
    // soft: if the method is not available (Desktop mode, a different
    // Playnite version) or throws, navigation is unaffected and the sound is
    // simply not played.
    //
    // When it plays - only for navigation the user is actually performing:
    //   * the main view brackets directional input (arrow keys and the D-pad,
    //     the single funnel that routes to every page and overlay) with
    //     BeginUserNavigation/EndUserNavigation; a focus change that happens
    //     outside that bracket - opening a page, restoring focus after a
    //     screen closes, a refresh, mouse clicks - is programmatic and silent;
    //   * the focus must genuinely move to a different, visible MediaCard or
    //     ToggleButton chip; the handler sits at the view root and receives
    //     each focus change exactly once;
    //   * a short minimum gap drops duplicate notifications for one action.
    // Controls Playnite already covers (including the season / episode
    // Buttons) are deliberately excluded so nothing ticks twice.
    internal static class NavigationSound
    {
        private const string PlayniteSoundType = "Playnite.FullscreenApp.FullscreenApplication";
        private const string PlayniteSoundMethod = "PlayNavigateSound";

        // Drops duplicate notifications for one navigation action; far below
        // the time between two deliberate key presses.
        private const double MinimumGapMilliseconds = 40;

        private static int _userNavigationDepth;
        private static long _lastPlayTimestamp;
        private static bool _resolved;
        private static Action _playniteTick;

        // Directional input about to be processed by the main view. UI thread
        // only; always paired in a try/finally.
        internal static void BeginUserNavigation()
        {
            _userNavigationDepth++;
        }

        internal static void EndUserNavigation()
        {
            if (_userNavigationDepth > 0)
            {
                _userNavigationDepth--;
            }
        }

        // Called for every keyboard-focus change inside the Video Center.
        internal static void OnKeyboardFocusChanged(KeyboardFocusChangedEventArgs e)
        {
            if (_userNavigationDepth <= 0)
            {
                return;
            }

            UIElement target = e.NewFocus as UIElement;

            if (target == null || ReferenceEquals(e.OldFocus, target))
            {
                return;
            }

            // Only the controls Playnite's own handler does not cover. A
            // plain ToggleButton only: CheckBox / RadioButton derive from it
            // and are already handled by Playnite.
            bool eligible =
                target is MediaCard ||
                target.GetType() == typeof(ToggleButton);

            if (!eligible || !target.IsVisible)
            {
                return;
            }

            // Same rule Playnite applies: a mouse press that moved focus is
            // not keyboard/controller navigation.
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                return;
            }

            Play();
        }

        private static void Play()
        {
            long now = Stopwatch.GetTimestamp();

            if (_lastPlayTimestamp != 0 &&
                (now - _lastPlayTimestamp) * 1000.0 / Stopwatch.Frequency < MinimumGapMilliseconds)
            {
                return;
            }

            _lastPlayTimestamp = now;

            Action tick = ResolvePlayniteTick();

            if (tick == null)
            {
                return;
            }

            try
            {
                tick();
            }
            catch (Exception ex)
            {
                // Never let a sound problem affect navigation; stop trying
                // and say why once.
                _playniteTick = null;

                StremioVideoCenterLogger.Info(
                    "Navigation sound disabled: Playnite's sound call failed. " +
                    UserFacingErrors.ForLog(ex));
            }
        }

        private static Action ResolvePlayniteTick()
        {
            if (_resolved)
            {
                return _playniteTick;
            }

            _resolved = true;

            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic)
                    {
                        continue;
                    }

                    Type type = assembly.GetType(PlayniteSoundType, false);

                    if (type == null)
                    {
                        continue;
                    }

                    MethodInfo method = type.GetMethod(
                        PlayniteSoundMethod,
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        Type.EmptyTypes,
                        null);

                    if (method != null && method.ReturnType == typeof(void))
                    {
                        _playniteTick = (Action)Delegate.CreateDelegate(typeof(Action), method);

                        return _playniteTick;
                    }
                }

                StremioVideoCenterLogger.Info(
                    "Navigation sound not available: Playnite's Fullscreen sound call was not found.");
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(
                    "Navigation sound not available: " + UserFacingErrors.ForLog(ex));
            }

            return null;
        }
    }
}
