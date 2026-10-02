using System;
using System.Text.RegularExpressions;

namespace StremioVideoCenter.Services
{
    // Turns playback/player exceptions into text that is safe to show in a
    // dialog or write to a log. Exception messages from the player stack,
    // HTTP stack or mpv can contain command JSON or stream/addon URLs (which
    // may embed credentials), so UI code must never show ex.Message
    // directly - it asks this class instead.
    public static class UserFacingErrors
    {
        private static readonly Regex UrlPattern =
            new Regex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://\S+", RegexOptions.Compiled);

        // Short, generic text for the user. Never includes the exception
        // message of an unrecognised exception type.
        public static string ForPlayback(Exception ex)
        {
            if (ex is MpvCommandException)
            {
                return "The video player did not respond. Try again, or choose another source.";
            }

            if (ex is TimeoutException)
            {
                return "Starting playback took too long. Try again, or choose another source.";
            }

            if (ex is OperationCanceledException)
            {
                return "Playback was cancelled.";
            }

            return "Playback could not be started. Try again, or choose another source.";
        }

        public static string ForPlayerStartup(Exception ex)
        {
            return "The video player could not be started. Close and reopen the player to try again.";
        }

        // For logging: exception type plus a message with any URL removed.
        public static string ForLog(Exception ex)
        {
            if (ex == null)
            {
                return "(no exception)";
            }

            return ex.GetType().Name + ": " + StripUrls(ex.Message);
        }

        public static string StripUrls(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return UrlPattern.Replace(text, "[url removed]");
        }
    }
}
