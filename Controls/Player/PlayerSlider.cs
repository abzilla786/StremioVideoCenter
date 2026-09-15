using System;
using System.Windows.Controls;

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

        public PlayerSlider()
        {
            StepAmount = 5.0;
        }

        public bool HandleLeft()
        {
            if (OnStepLeft == null)
                return false;

            OnStepLeft(StepAmount);
            return true;
        }

        public bool HandleRight()
        {
            if (OnStepRight == null)
                return false;

            OnStepRight(StepAmount);
            return true;
        }
    }
}
