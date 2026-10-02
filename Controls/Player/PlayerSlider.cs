using System;
using System.Windows.Controls;
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

        public PlayerSlider()
        {
            StepAmount = 5.0;
            LostKeyboardFocus += (s, e) =>
            {
                IsEngaged = false;
                Background = Brushes.Transparent;
            };
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
