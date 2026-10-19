using System;
using System.IO;
using System.Text;
using System.Threading;

namespace StremioVideoCenter.Services
{
    // Single, application-wide log for StremioVideoCenter-side diagnostics -
    // as distinct from Playnite integration diagnostics, which continue to
    // use Playnite's own ILogger (see StremioVideoCenter.cs). Before this
    // class existed, diagnostic instrumentation added over time (mpv
    // startup-sequence tracing, ad hoc Desktop Mode investigation logging)
    // each wrote its own separate file under %TEMP%, so a run's worth of
    // diagnostics was scattered across several files with no single place
    // to look. This is the replacement: one stable file, appended to
    // across the whole plugin lifetime rather than recreated per run or
    // per attempt, stored in the extension's own Playnite user-data
    // folder rather than an ad hoc temp path.
    //
    // Must be initialized once, early, via Initialize(pluginUserDataPath)
    // - typically from the plugin constructor, passing
    // GetPluginUserDataPath(). Every write is best-effort: a failure here
    // (missing folder, locked file, permissions) is swallowed so that
    // logging itself can never crash or otherwise affect the application.
    internal static class StremioVideoCenterLogger
    {
        private const string LogFileName = "StremioVideoCenter.log";

        // Slice 20 Stage 1A: size-based rotation. When the active log would
        // grow past MaxLogBytes it is moved to "<name>.1" (replacing any
        // earlier backup) and a fresh log is started, so the total on disk
        // stays around twice this size instead of growing without limit.
        // The rotated file is redacted in the background so content
        // written before redaction existed is not kept.
        private const long MaxLogBytes = 5L * 1024 * 1024;

        private static readonly object _writeLock = new object();
        private static string _logFilePath;
        private static long _currentBytes;

        public static void Initialize(string pluginUserDataPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                {
                    return;
                }

                Directory.CreateDirectory(pluginUserDataPath);

                string logFilePath = Path.Combine(pluginUserDataPath, LogFileName);

                lock (_writeLock)
                {
                    _logFilePath = logFilePath;
                    _currentBytes = File.Exists(logFilePath)
                        ? new FileInfo(logFilePath).Length
                        : 0;

                    RotateIfNeeded(0);
                }
            }
            catch
            {
                // No writable location available - Write() below no-ops
                // safely for the rest of the process lifetime.
                _logFilePath = null;
            }
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Warn(string message, Exception ex)
        {
            Write("WARN", message, ex);
        }

        public static void Error(string message)
        {
            Write("ERROR", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            string logFilePath = _logFilePath;

            if (logFilePath == null)
            {
                return;
            }

            // Slice 20 Stage 1A: nothing written here may contain a URL
            // (addon URLs can embed a Real-Debrid key) or a full local
            // path (user name / folder layout). This also covers the
            // exception text, whose message and stack trace can contain
            // both.
            string line = string.Format(
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [thread {2}] {3}",
                DateTime.Now,
                level,
                Thread.CurrentThread.ManagedThreadId,
                UserFacingErrors.StripUrls(message));

            if (ex != null)
            {
                line += Environment.NewLine + UserFacingErrors.StripUrls(ex.ToString());
            }

            line += Environment.NewLine;

            lock (_writeLock)
            {
                try
                {
                    UTF8Encoding encoding = new UTF8Encoding(false);

                    RotateIfNeeded(encoding.GetByteCount(line));

                    File.AppendAllText(logFilePath, line, encoding);

                    _currentBytes += encoding.GetByteCount(line);
                }
                catch
                {
                    // Logging must never crash the application.
                }
            }
        }

        // Must be called with _writeLock held (or before logging starts).
        // Best-effort: if the file cannot be moved (e.g. another process
        // has it open) logging simply carries on appending.
        private static void RotateIfNeeded(int incomingBytes)
        {
            string logFilePath = _logFilePath;

            if (logFilePath == null ||
                _currentBytes == 0 ||
                _currentBytes + incomingBytes <= MaxLogBytes)
            {
                return;
            }

            try
            {
                string backupPath = logFilePath + ".1";

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                File.Move(logFilePath, backupPath);

                _currentBytes = 0;

                ThreadPool.QueueUserWorkItem(
                    state => LogFileRedactor.RedactInPlace((string)state, false),
                    backupPath);
            }
            catch
            {
                // Rotation failed. Restart the count rather than retrying the
                // move on every write; it is attempted again after another
                // MaxLogBytes of output.
                _currentBytes = 0;
            }
        }
    }
}
