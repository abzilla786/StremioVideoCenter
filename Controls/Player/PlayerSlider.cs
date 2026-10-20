using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace StremioVideoCenterControls.Player
{
    // A Slider that intercepts Left/Right itself (see IDirectionalInputConsumer)
    // instead of letting PlayerFocusController move focus to the next control.
    // MpvPlayer wires OnStepLeft/OnStepRight per-slider (seek vs. volume) so the
    // focus controller never needs to know which slider is which.
    public sealed class PlayerSlider : Slider, IDirectionalInputConsumer
    {
        public Action<double> OnStepLeft { get; set; }
        public Action<double> OnStepRight { get; set; }
        public double StepAmount { get; set; }

        // When true, Left/Right only adjust the slider after it has been
        // "engaged" (A/Enter), and otherwise move focus along the row. This
        // stops a slider in the middle of a button row from trapping
        // controller/keyboard focus. The seek slider (alone in its row)
        // leaves this false and always consumes Left/Right.
        public bool RequiresEngagement { get; set; }

        public bool IsEngaged { get; private set; }

        // > 0 while a position pushed in by the player (not chosen by the
        // user) is being applied. See ApplyPlaybackUpdate.
        private int _playbackUpdateDepth;

        static PlayerSlider()
        {
            // Registered on PlayerSlider itself: WPF runs a derived class's
            // static class handler BEFORE the base classes' (documented class-
            // handling order), so this sees ValueChanged ahead of any handler
            // the host registered on Slider.
            EventManager.RegisterClassHandler(
                typeof(PlayerSlider),
                RangeBase.ValueChangedEvent,
                new RoutedPropertyChangedEventHandler<double>(OnValueChangedClassHandler));
        }

        public PlayerSlider()
        {
            StepAmount = 5.0;
            LostKeyboardFocus += (s, e) =>
            {
                IsEngaged = false;
                Background = Brushes.Transparent;
            };
        }

        // Applies a value that comes from the player's own clock (the
        // periodic position poll) rather than from the user. The seek bar
        // changes value about twice a second during playback; the host can
        // key feedback on Slider.ValueChanged - Playnite's Fullscreen mode
        // plays its "activation" sound for EVERY ValueChanged of a focused
        // Slider - so a focused seek bar would tick for as long as the video
        // plays. ValueChanged raised by changes made inside this call is
        // marked handled (see OnValueChangedClassHandler).
        public void ApplyPlaybackUpdate(Action update)
        {
            if (update == null) return;

            _playbackUpdateDepth++;
            try
            {
                update();
            }
            finally
            {
                _playbackUpdateDepth--;
            }
        }

        // The whole WPF pipeline still runs for playback updates - RangeBase
        // and Slider processing, the ValueChanged routed event and UI
        // Automation notifications are all untouched. The only effect is that
        // the event is marked Handled, which makes class handlers registered
        // without handledEventsToo (such as Playnite Fullscreen's feedback
        // sound on Slider) skip it. The guard is per slider and only true
        // inside ApplyPlaybackUpdate, so changes made by the user - mouse,
        // keyboard, controller, drag - and every other PlayerSlider (the
        // volume slider never uses ApplyPlaybackUpdate) are not affected.
        private static void OnValueChangedClassHandler(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PlayerSlider slider = sender as PlayerSlider;
            if (slider != null && slider._playbackUpdateDepth > 0)
                e.Handled = true;
        }

        // Toggles engagement; returns the new state.
        public bool ToggleEngaged()
        {
            IsEngaged = !IsEngaged;
            return IsEngaged;
        }

        public void Disengage()
        {
            IsEngaged = false;
        }

        private bool CanConsumeDirectional
        {
            get { return !RequiresEngagement || IsEngaged; }
        }

        public bool HandleLeft()
        {
            if (OnStepLeft == null || !CanConsumeDirectional)
                return false;

            OnStepLeft(StepAmount);
            return true;
        }

        public bool HandleRight()
        {
            if (OnStepRight == null || !CanConsumeDirectional)
                return false;

            OnStepRight(StepAmount);
            return true;
        }
    }
}
