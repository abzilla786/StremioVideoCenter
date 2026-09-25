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

        private static readonly object _writeLock = new object();
        private static string _logFilePath;

        public static void Initialize(string pluginUserDataPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath))
                {
                    return;
                }

                Directory.CreateDirectory(pluginUserDataPath);

                _logFilePath = Path.Combine(pluginUserDataPath, LogFileName);
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

            string line = string.Format(
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [thread {2}] {3}",
                DateTime.Now,
                level,
                Thread.CurrentThread.ManagedThreadId,
                message);

            if (ex != null)
            {
                line += Environment.NewLine + ex;
            }

            lock (_writeLock)
            {
                try
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never crash the application.
                }
            }
        }
    }
}
