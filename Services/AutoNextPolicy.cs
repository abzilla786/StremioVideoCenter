using System;

namespace StremioVideoCenter.Services
{
    // How an mpv end-file event is interpreted. Only a genuine end of file
    // (reason=eof) is ever eligible for completion / auto-next; error, stop,
    // quit and redirect never are.
    public enum EndFileKind
    {
        Other,
        Eof,
        Failure,
        Stopped
    }

    public static class PlaybackEndClassifier
    {
        public static EndFileKind Classify(string reason, string fileError)
        {
            if (string.Equals(reason, "eof", StringComparison.OrdinalIgnoreCase))
                return EndFileKind.Eof;

            if (string.Equals(reason, "error", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(fileError) ? EndFileKind.Other : EndFileKind.Failure;

            if (string.Equals(reason, "stop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "quit", StringComparison.OrdinalIgnoreCase))
                return EndFileKind.Stopped;

            return EndFileKind.Other;
        }
    }

    public static class AutoNextPolicy
    {
        // Auto-next starts the next episode only when ALL hold:
        //  - mpv reported a genuine end of file for the established file;
        //  - the player is still open and playing this attempt;
        //  - a next episode exists;
        //  - no episode transition is already running;
        //  - the user is not closing the player.
        public static bool ShouldAutoNext(
            bool genuineEndOfFile,
            bool playbackWasEstablished,
            bool hasNextEpisode,
            bool transitionInProgress,
            bool playerClosing)
        {
            return genuineEndOfFile &&
                   playbackWasEstablished &&
                   hasNextEpisode &&
                   !transitionInProgress &&
                   !playerClosing;
        }
    }
}
