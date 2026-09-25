using System.Threading;
using StremioVideoCenter.Services;

namespace StremioVideoCenterControls.Player
{
    // Startup-sequencing diagnostic aid: tags each mpv launch attempt with
    // a sequential id so overlapping PlayerSession.StartAsync calls can be
    // proven or disproven from real evidence instead of inference. Writes
    // through the shared application log (StremioVideoCenterLogger) rather
    // than its own separate %TEMP% file, so a sequence of startup attempts
    // shows up in the same place as every other application diagnostic.
    // Call sites in PlayerSession.cs and MpvPlayer.xaml.cs are unchanged -
    // only where the messages end up has changed.
    internal static class StartupTrace
    {
        private static int _nextAttemptId;

        // Call once per genuine PlayerSession.StartAsync attempt, right
        // before it begins, so every "attempt N" in the trace log
        // unambiguously identifies one specific mpv launch.
        public static int NewAttemptId()
        {
            return Interlocked.Increment(ref _nextAttemptId);
        }

        public static void Log(string message)
        {
            StremioVideoCenterLogger.Debug("[MpvStartup] " + message);
        }
    }
}
