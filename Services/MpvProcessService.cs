using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
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

        // Slice 22: Windows Job Object (kill-on-close) owned by this
        // instance and holding only the mpv process this instance started.
        // It never terminates mpv while Zino is running - the handle stays
        // open for the whole life of the process - and exists only so that
        // if Playnite is killed or crashes (so no cleanup code runs) the OS
        // closes the handle and ends the orphaned mpv. Best-effort: if the
        // job cannot be created or assigned, playback is unaffected.
        private IntPtr _jobHandle = IntPtr.Zero;

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
            get { return DefaultLogFilePath; }
        }

        private static string DefaultLogFilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "StremioVideoCenter_Mpv.log"); }
        }

        // Slice 20 Stage 1A: called once when the plugin starts. A log left
        // by an earlier run that never reached the exit-time redaction
        // (crash, Playnite killed) is redacted on a background thread so
        // plugin startup is never delayed. A missing or locked log is
        // simply skipped; the diagnostics written here are fixed text plus
        // an exception type name and never include log contents or paths.
        public static void RedactStaleLogBestEffort()
        {
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        if (!LogFileRedactor.RedactInPlace(DefaultLogFilePath, true))
                        {
                            StremioVideoCenterLogger.Warn(
                                "Stale MPV log could not be redacted at startup (file in use or not accessible); it will be retried at the next player start.");
                        }
                    }
                    catch (Exception ex)
                    {
                        StremioVideoCenterLogger.Warn(
                            "Stale MPV log redaction at startup failed: " + ex.GetType().Name);
                    }
                });
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "Stale MPV log redaction could not be scheduled: " + ex.GetType().Name);
            }
        }

        // Slice 20 Stage 1A: mpv writes its own log, and that log contains
        // the stream URL (which can embed a Real-Debrid key or signed
        // token), subtitle URLs and full local paths.
        //
        // The URLs and paths cannot be prevented: mpv's curl and
        // stream_callback modules log every URL they open, its player core
        // logs the loadfile command and "Opening done" line, and option
        // values such as the log path appear too (--msg-level does not
        // filter the --log-file output - verified at runtime). So the log
        // is rewritten with URLs and paths removed once mpv has exited,
        // at the next player start, and at plugin start, for a log left by
        // a crash. Everything else in the log
        // (timestamps, levels, modules, codec, track, renderer, audio and
        // error lines) is kept as written.
        //
        // Never throws. If mpv still has the file open the rewrite is
        // skipped and retried at the next start.
        private void RedactLog()
        {
            bool clean = LogFileRedactor.RedactInPlace(LogFilePath, true);

            if (!clean)
            {
                StremioVideoCenterLogger.Warn(
                    "The MPV log could not be redacted yet (file in use); it will be redacted at the next player start.");
            }
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

            // A log left behind by an earlier run (for example after a
            // crash, when the exit-time redaction below never ran) is
            // cleaned before anything else touches it.
            RedactLog();

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

            AttachProcessToKillOnCloseJob(_process);

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

            // Slice 22: the mpv bundled with THIS plugin comes first, so a
            // stray mpv folder next to Playnite's own executable (or in its
            // current directory) cannot shadow it. The other locations stay
            // as fallbacks in their previous order.
            string pluginDirectory = null;

            try
            {
                pluginDirectory = Path.GetDirectoryName(typeof(MpvProcessService).Assembly.Location);
            }
            catch (Exception)
            {
                pluginDirectory = null;
            }

            if (!string.IsNullOrEmpty(pluginDirectory))
            {
                string pluginLocal = Path.Combine(pluginDirectory, "mpv", "mpv.exe");

                if (IsUsableExecutable(pluginLocal))
                    return Path.GetFullPath(pluginLocal);
            }

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
                if (!string.IsNullOrWhiteSpace(candidate) && IsUsableExecutable(candidate))
                    return Path.GetFullPath(candidate);
            }

            return null;
        }

        // Exists and is not an empty/truncated file (a partially copied
        // mpv.exe). Not a signature check - only a cheap sanity filter.
        private static bool IsUsableExecutable(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                return info.Exists && info.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // =========================================================
        // KILL-ON-CLOSE JOB OBJECT (orphan protection)
        // =========================================================

        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobIoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public JobIoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        // Never throws. Only the process this instance just started is
        // assigned; no other process is touched.
        private void AttachProcessToKillOnCloseJob(Process process)
        {
            IntPtr job = IntPtr.Zero;
            IntPtr infoPointer = IntPtr.Zero;

            try
            {
                job = CreateJobObject(IntPtr.Zero, null);

                if (job == IntPtr.Zero)
                    throw new InvalidOperationException("CreateJobObject failed.");

                JobExtendedLimitInformation info = new JobExtendedLimitInformation();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                int length = Marshal.SizeOf(typeof(JobExtendedLimitInformation));
                infoPointer = Marshal.AllocHGlobal(length);
                Marshal.StructureToPtr(info, infoPointer, false);

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, infoPointer, (uint)length))
                    throw new InvalidOperationException("SetInformationJobObject failed.");

                if (!AssignProcessToJobObject(job, process.Handle))
                    throw new InvalidOperationException("AssignProcessToJobObject failed.");

                _jobHandle = job;
                job = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Warn(
                    "MPV orphan protection (job object) could not be set up; playback is unaffected: " +
                    ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                if (infoPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(infoPointer);

                if (job != IntPtr.Zero)
                    CloseHandle(job);
            }
        }

        private void CloseJob()
        {
            IntPtr job = _jobHandle;
            _jobHandle = IntPtr.Zero;

            if (job != IntPtr.Zero)
                CloseHandle(job);
        }

        // Waits up to timeoutMilliseconds for mpv to exit on its own (e.g.
        // after a graceful "quit" command). Returns true if it has exited.
        // Never throws.
        public bool WaitForExit(int timeoutMilliseconds)
        {
            try
            {
                Process process = _process;
                return process == null || process.HasExited || process.WaitForExit(timeoutMilliseconds);
            }
            catch
            {
                return true;
            }
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

            // The process this job protected is gone (or was killed above);
            // closing the handle now cannot affect anything else.
            CloseJob();

            // mpv has exited (or the wait above timed out, in which case
            // RedactLog leaves the still-open file for the next start).
            RedactLog();
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
