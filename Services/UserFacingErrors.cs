using System;
using System.Collections.Generic;
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
        internal const string UrlReplacement = "[url removed]";
        internal const string PathReplacement = "[path removed]";
        internal const string CredentialReplacement = "[credential removed]";

        private const RegexOptions CredentialOptions =
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // magnet:?xt=urn:btih:... links have no "://" so the URL pattern
        // misses them; they identify a specific torrent.
        private static readonly Regex MagnetPattern =
            new Regex(@"(?<![A-Za-z0-9])magnet:\?\S+", CredentialOptions);

        // "Authorization: <scheme> <value>" - the whole header value.
        private static readonly Regex AuthorizationPattern =
            new Regex(
                @"(Authorization\s*[:=]\s*)(?!\[(?:credential|url|path) removed\])(?:(?:Bearer|Basic|Token|Digest)\s+)?[^\s,;""']+",
                CredentialOptions);

        // A bare "Bearer <token>" (token of 8+ characters, so ordinary
        // prose such as "bearer of bad news" is left alone).
        private static readonly Regex BearerPattern =
            new Regex(@"(?<![A-Za-z0-9])(Bearer\s+)[A-Za-z0-9\-._~+/]{8,}=*", CredentialOptions);

        // key=value assignments: api_key, apikey, api-key, token,
        // access_token, auth-token, password, passwd, pwd, secret,
        // client_secret ... (any prefix such as "realdebrid_token" is
        // allowed; the key must END with one of the names, so "tokens=5"
        // and "keyboard=us" are not matched). Quoted (optionally with a
        // quoted key, as in JSON) or bare values. A bare value made only of
        // digits is left alone (the search diagnostic logs "token=3"; a
        // purely numeric credential is not a realistic case here).
        private static readonly Regex CredentialEqualsPattern =
            new Regex(
                @"((?<![A-Za-z0-9])[A-Za-z0-9_\-]*?(?:api[_-]?key|token|password|passwd|pwd|secret)[""']?\s*=\s*)(?!\[(?:credential|url|path) removed\])(""[^""]*""|'[^']*'|(?![0-9]+(?![^\s&;,""')\]]))[^\s&;,""')\]]+)",
                CredentialOptions);

        // key: value assignments are far more likely to be ordinary prose
        // ("Token: expired", "password: required"), so only a value that
        // looks like a credential is removed: a quoted string, or an
        // unquoted run of 16+ token characters.
        private static readonly Regex CredentialColonPattern =
            new Regex(
                @"((?<![A-Za-z0-9])[A-Za-z0-9_\-]*?(?:api[_-]?key|token|password|passwd|pwd|secret)[""']?\s*:\s*)(?!\[(?:credential|url|path) removed\])(""[^""]+""|'[^']+'|[A-Za-z0-9._~+/\-]{16,}=*)",
                CredentialOptions);

        private static readonly Regex UrlPattern =
            new Regex(@"[a-zA-Z][a-zA-Z0-9+.\-]*://\S+", RegexOptions.Compiled);

        // Every credential pattern skips a value that is already one of our
        // own "[... removed]" markers, so redacting text twice (for
        // example a rotated log that was already redacted) changes nothing.

        // Windows drive paths (C:\..., C:/...) and UNC paths (\\server\share\...).
        // Directory segments may contain spaces; the final segment ends at
        // whitespace or a quote so the rest of an error sentence survives.
        // \\.\ and \\?\ device paths are not user paths and are left alone.
        private static readonly Regex PathPattern =
            new Regex(
                @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\(?![.?][\\/])[^\\/\s""'<>|*?]+[\\/])(?:[^\\/\r\n""'<>|*?]*[\\/])*[^\\/\s""'<>|*?]*",
                RegexOptions.Compiled);

        // Short, generic text for the user. Never includes the exception
        // message of an unrecognised exception type.
        public static string ForPlayback(Exception ex)
        {
            if (ex is PlayerStartupException)
            {
                return ForPlayerStartup(ex);
            }

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
            return "The video player could not be started. Try again.";
        }

        // =========================================================
        // SLICE 20 STAGE 2 - WORDING FOR NON-BLOCKING PLAYBACK NOTICES
        // =========================================================
        //
        // Every string here is fixed text chosen from a category: nothing
        // is built from an exception message, URL, addon name, source
        // title or response. The categories are internal (see
        // RealDebridFailureReason / UnresolvedCause); this is the only
        // place they become words, and each wording claims no more than
        // the category's evidence establishes.

        // Discovery produced no stream at all. Addon failures are not
        // reported by discovery (a failed addon and an addon with nothing
        // to offer both yield an empty list), so this does not claim
        // either.
        public static string ForNoSources()
        {
            return "No addon returned a source for this title. If this keeps happening, check your addons in Settings.";
        }

        // Streams were found but none became playable. Explains WHY only
        // when every attempted resolution failed for the same classified
        // Real-Debrid reason; otherwise a plain "none could be prepared".
        public static string ForUnresolvedSources(IList<UnresolvedSource> unresolved)
        {
            if (unresolved == null || unresolved.Count == 0)
            {
                return ForNoSources();
            }

            int attempted = 0;
            int classified = 0;
            RealDebridFailureReason? only = null;
            bool mixed = false;

            foreach (UnresolvedSource item in unresolved)
            {
                if (item == null || item.Cause != UnresolvedCause.ResolutionFailed)
                {
                    continue;
                }

                attempted++;

                if (!item.DebridReason.HasValue)
                {
                    continue;
                }

                classified++;

                if (!only.HasValue)
                {
                    only = item.DebridReason;
                }
                else if (only.Value != item.DebridReason.Value)
                {
                    mixed = true;
                }
            }

            if (attempted == 0)
            {
                return "The available sources can't be played inside the app.";
            }

            if (classified == attempted && !mixed && only.HasValue)
            {
                return ForRealDebrid(only.Value);
            }

            if (classified == attempted)
            {
                return "Real-Debrid couldn't prepare any of the available sources.";
            }

            return "None of the available sources could be prepared for playback.";
        }

        public static string ForRealDebrid(RealDebridFailureReason reason)
        {
            switch (reason)
            {
                case RealDebridFailureReason.NotConfigured:
                    return "These sources need Real-Debrid, but no Real-Debrid key is set up. Add one in Settings.";
                case RealDebridFailureReason.Authentication:
                    return "Real-Debrid refused the request. Check your Real-Debrid key and account in Settings.";
                case RealDebridFailureReason.RateLimited:
                    return "Real-Debrid is limiting requests right now. Wait a moment, then try again.";
                case RealDebridFailureReason.Timeout:
                    return "Real-Debrid didn't respond in time. Try again.";
                case RealDebridFailureReason.Network:
                    return "Couldn't reach Real-Debrid. Check your connection, then try again.";
                case RealDebridFailureReason.ResourceUnavailable:
                    return "Real-Debrid couldn't use these sources.";
                case RealDebridFailureReason.NotReadyInTime:
                    return "Real-Debrid didn't finish preparing these sources in time.";
                case RealDebridFailureReason.NoPlayableResult:
                    return "Real-Debrid didn't return a playable link for these sources.";
                case RealDebridFailureReason.ServiceFailure:
                    return "Real-Debrid reported a service problem. Try again later.";
                default:
                    return "Real-Debrid couldn't prepare these sources.";
            }
        }

        // mpv accepted the file but then reported it could not be opened
        // or played (end-file with an error).
        public static string ForPlaybackFailedAfterStart()
        {
            return "That source couldn't be played. Choose another source.";
        }

        // For logging: exception type plus a message with any URL removed.
        public static string ForLog(Exception ex)
        {
            if (ex == null)
            {
                return "(no exception)";
            }

            string text = ex.GetType().Name + ": " + StripUrls(ex.Message);

            // The startup wrapper has a fixed message; the real cause is
            // its inner exception (also stripped), which the log needs.
            PlayerStartupException startup = ex as PlayerStartupException;

            if (startup != null && startup.InnerException != null)
            {
                text += " (" + ForLog(startup.InnerException) + ")";
            }

            return text;
        }

        // Removes URLs (which may embed credentials or signed tokens) and
        // full local paths (Windows drive paths and UNC paths, which expose
        // the user name and folder layout). Safe to call on any text that is
        // going to a log or a dialog.
        public static string StripUrls(string text)
        {
            return RemovePaths(RemoveUrls(text));
        }

        // Removes URLs and the other ways a credential reaches a log line:
        // magnet links, Authorization / Bearer values and key=value style
        // api-key / token / password assignments. (The name predates the
        // credential patterns; kept so existing callers are unchanged.)
        internal static string RemoveUrls(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string result = UrlPattern.Replace(text, UrlReplacement);

            result = MagnetPattern.Replace(result, UrlReplacement);
            result = AuthorizationPattern.Replace(result, "$1" + CredentialReplacement);
            result = BearerPattern.Replace(result, "$1" + CredentialReplacement);
            result = CredentialEqualsPattern.Replace(result, "$1" + CredentialReplacement);
            result = CredentialColonPattern.Replace(result, "$1" + CredentialReplacement);

            return result;
        }

        internal static string RemovePaths(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return PathPattern.Replace(text, PathReplacement);
        }
    }

    // Slice 20 Stage 2: the mpv process/IPC could not be started or
    // connected. Wraps the original failure (kept as InnerException for
    // the log, which is URL/path-stripped by UserFacingErrors.ForLog) so
    // the caller can tell "the player never started" from "the player
    // refused this file". Its own message is fixed.
    public sealed class PlayerStartupException : Exception
    {
        public PlayerStartupException(Exception inner)
            : base("The video player could not be started.", inner)
        {
        }
    }
}
