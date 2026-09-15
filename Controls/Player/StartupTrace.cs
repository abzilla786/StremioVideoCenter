using System;
using System.IO;
using System.Threading;

namespace StremioVideoCenterControls.Player
{
    // Temporary diagnostic aid: writes timestamped, thread-tagged lines to
    // %TEMP%\StremioVideoCenter_StartupTrace.log so overlapping mpv startup
    // calls can be proven or disproven from real evidence instead of
    // inference. Not part of the normal mpv per-launch log rotation/cleanup
    // story - this is its own single file, appended to across the whole
    // process lifetime (intentionally, so a sequence of attempts stays
    // together in one place to read).
    internal static class StartupTrace
    {
        private static readonly object _writeLock = new object();
        private static int _nextAttemptId;

        public static string LogFilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "StremioVideoCenter_StartupTrace.log"); }
        }

        // Call once per genuine PlayerSession.StartAsync attempt, right
        // before it begins, so every "attempt N" in the trace log
        // unambiguously identifies one specific mpv launch.
        public static int NewAttemptId()
        {
            return Interlocked.Increment(ref _nextAttemptId);
        }

        public static void Log(string message)
        {
            string line = string.Format(
                "[{0:HH:mm:ss.fff}] [thread {1}] {2}",
                DateTime.Now,
                Thread.CurrentThread.ManagedThreadId,
                message);

            lock (_writeLock)
            {
                try { File.AppendAllText(LogFilePath, line + Environment.NewLine); }
                catch { }
            }
        }
    }
}
