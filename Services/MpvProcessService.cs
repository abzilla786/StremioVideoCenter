using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Owns only the mpv.exe process and the raw named-pipe connection to it.
    // The JSON IPC protocol (requests/replies/events) lives entirely in
    // MpvIpcClient - this class never parses a line of mpv's output.
    //
    // Launch arguments are unchanged from the known-good configuration.
    // Do not add renderer flags here without a specific, tested reason.
    public sealed class MpvProcessService : IDisposable
    {
        private readonly string _pipeName;
        private Process _process;
        private bool _disposed;

        public MpvProcessService()
        {
            _pipeName = "StremioVideoCenter_Mpv_" + Guid.NewGuid().ToString("N");
        }

        public bool IsRunning
        {
            get { return _process != null && !_process.HasExited; }
        }

        public int ProcessId
        {
            get
            {
                try { return _process != null && !_process.HasExited ? _process.Id : 0; }
                catch { return 0; }
            }
        }

        public int ExitCodeOrDefault
        {
            get
            {
                try { return _process != null && _process.HasExited ? _process.ExitCode : -1; }
                catch { return -1; }
            }
        }

        // Single stable file, overwritten by mpv (--log-file truncates) on
        // each new session, so the latest log is always at one known path.
        // Deliberately NOT derived from _pipeName: the IPC pipe name must
        // stay unique per instance, but the log filename need not be.
        public string LogFilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "StremioVideoCenter_Mpv.log"); }
        }

        // Launches mpv.exe embedded into hostHandle and connects the JSON IPC
        // pipe. Returns the connected pipe stream; the caller (PlayerSession)
        // hands it to MpvIpcClient, which owns the protocol from that point on.
        public async Task<NamedPipeClientStream> StartAsync(IntPtr hostHandle)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MpvProcessService));

            string executablePath = FindMpvExecutable();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new FileNotFoundException(
                    "mpv.exe could not be found. Expected an 'mpv' folder containing mpv.exe.");
            }

            string workingDirectory = Path.GetDirectoryName(executablePath);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments =
                        "--player-operation-mode=cplayer" +
                        " --wid=" + hostHandle.ToInt64() +
                        " --no-config" +
                        " --idle=yes" +
                        " --keep-open=yes" +
                        " --keep-open-pause=yes" +
                        " --force-window=yes" +
                        " --no-osc" +
                        " --osd-level=0" +
                        " --input-terminal=no" +
                        " --input-default-bindings=no" +
                        " --input-builtin-bindings=no" +
                        " --input-vo-keyboard=no" +
                        " --input-cursor=no" +
                        " --input-builtin-dragging=no" +
                        " --input-builtin-drag-and-drop=no" +
                        " --log-file=\"" + LogFilePath + "\"" +
                        " --input-ipc-server=\\\\.\\pipe\\" + _pipeName
                }
            };

            if (!_process.Start())
                throw new InvalidOperationException("Unable to start mpv.exe.");

            NamedPipeClientStream pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(5000).ConfigureAwait(true);
            }
            catch
            {
                KillAndDisposeProcess();
                pipe.Dispose();
                throw new InvalidOperationException(
                    "mpv started, but its JSON IPC pipe did not become available. " +
                    "See the MPV log: " + LogFilePath);
            }

            // Give mpv a moment to finish initialising before we trust IsRunning.
            await Task.Delay(150).ConfigureAwait(true);

            if (!IsRunning)
            {
                int exitCode = ExitCodeOrDefault;
                KillAndDisposeProcess();
                pipe.Dispose();
                throw new InvalidOperationException(
                    "mpv started but exited during initialisation. Exit code: " + exitCode +
                    ". See the MPV log: " + LogFilePath);
            }

            return pipe;
        }

        private static string FindMpvExecutable()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string currentDirectory = Environment.CurrentDirectory;

            string[] candidates =
            {
                Path.Combine(baseDirectory, "mpv", "mpv.exe"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "mpv", "mpv.exe")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "mpv", "mpv.exe")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "mpv", "mpv.exe")),
                Path.Combine(currentDirectory, "mpv", "mpv.exe"),
                Path.Combine(Path.GetDirectoryName(typeof(MpvProcessService).Assembly.Location) ?? string.Empty, "mpv", "mpv.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        private void KillAndDisposeProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { }

            // The mpv child window that gets reparented under MpvHost's native
            // window belongs to this process and is only destroyed by the OS
            // once the process has fully exited. The previous version called
            // WaitForExit(1000) but discarded its result, so a slow-to-exit
            // mpv process could still be tearing its window down when the next
            // StartAsync() began searching for a new one. Confirm exit (with
            // one forced retry) before proceeding, so callers can rely on the
            // old HWND being gone by the time this method returns.
            if (_process != null)
                WaitForProcessExit(_process, 2000);

            try { _process?.Dispose(); } catch { }
            _process = null;
        }

        // Best-effort, bounded confirmation that a killed process has actually
        // exited. Never throws - matches the rest of this class's teardown style.
        private static void WaitForProcessExit(Process process, int timeoutMilliseconds)
        {
            try
            {
                if (process.WaitForExit(timeoutMilliseconds))
                    return;
            }
            catch { return; }

            // Still alive after the first window: one forced retry, bounded,
            // rather than silently trusting a timed-out wait and moving on.
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }

            try { process.WaitForExit(1000); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            KillAndDisposeProcess();
        }
    }
}
