using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOG FILE REDACTOR (Slice 20 Stage 1A)
    // =============================================================
    //
    // Removes URLs (which may embed Real-Debrid keys or signed tokens) and
    // full Windows / UNC paths from a log file after it has been written.
    //
    // Used for:
    //   - the mpv log (%TEMP%\StremioVideoCenter_Mpv.log), which mpv writes
    //     itself and which contains the stream URL and local paths in its
    //     own diagnostic lines, and
    //   - the previous StremioVideoCenter.log when it is rotated, so
    //     content written before redaction existed is not retained.
    //
    // Redaction is line based and never throws. If the file cannot be
    // rewritten (for example mpv still has it open) the file is left as it
    // was and false is returned; the caller decides when to try again.
    internal static class LogFileRedactor
    {
        // For mpv lines the path runs to the next quote/comma/bracket or the
        // end of the line, so a file name containing spaces is removed too.
        // \\.\ and \\?\ device paths (pipes, displays) are not user paths
        // and are left alone.
        private static readonly Regex MpvPathPattern =
            new Regex(
                @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\(?![.?][\\/])[^\\/\s""'<>|*?]+[\\/])[^\r\n""'<>|*?,\]]*",
                RegexOptions.Compiled);

        public static string RedactLine(string line, bool mpvLine)
        {
            if (string.IsNullOrEmpty(line))
            {
                return line;
            }

            // URLs first, so a path-like URL tail is never mistaken for a path.
            string redacted = UserFacingErrors.RemoveUrls(line);

            if (mpvLine)
            {
                return MpvPathPattern.Replace(redacted, UserFacingErrors.PathReplacement);
            }

            return UserFacingErrors.RemovePaths(redacted);
        }

        // Rewrites the file in place with every line redacted. Returns true
        // when the file is clean afterwards (rewritten, or nothing to
        // redact, or no such file); false when it could not be rewritten.
        public static bool RedactInPlace(string path, bool mpvLog)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            // One rewrite at a time in this process (the plugin-start
            // cleanup, a player start and a log rotation could otherwise
            // overlap on the same temp file).
            lock (RewriteLock)
            {
                return RedactInPlaceCore(path, mpvLog);
            }
        }

        private static readonly object RewriteLock = new object();

        private static bool RedactInPlaceCore(string path, bool mpvLog)
        {
            string temp = path + ".redact.tmp";

            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                bool changed = false;

                using (FileStream input = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(input, Encoding.UTF8, true))
                using (StreamWriter writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        string redacted = RedactLine(line, mpvLog);

                        if (!string.Equals(redacted, line, StringComparison.Ordinal))
                        {
                            changed = true;
                        }

                        writer.WriteLine(redacted);
                    }
                }

                if (!changed)
                {
                    File.Delete(temp);
                    return true;
                }

                File.Replace(temp, path, null);

                return true;
            }
            catch (Exception)
            {
                // Locked, missing permission, disk full...: the file is left
                // as it was and the caller is told it is not yet clean. This
                // also runs on a thread-pool thread, so it must not throw.
                TryDelete(temp);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
