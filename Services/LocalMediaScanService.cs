using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA SCAN SERVICE
    // =============================================================
    //
    // Filesystem discovery only - recursively finds supported video
    // files under one LibraryLocation and hands each one to
    // LocalMediaIdentificationService for filename-based classification.
    // Separate from LocalLibraryService (which owns WHICH locations
    // exist), LocalMediaIdentificationService (HOW a filename is parsed)
    // and LocalLibraryManager (when scans run and what happens to the
    // results).
    //
    // Safety properties (all required by the Local Files feature):
    //   * Runs entirely on a thread-pool thread; never touches the UI.
    //   * Cancellable at every directory entry; a cancelled scan returns
    //     what it had with WasCancelled set.
    //   * Directory junctions / symbolic links / any directory carrying the
    //     reparse-point attribute are NOT followed, so a link cannot create
    //     a loop or walk into an arbitrary tree. (File reparse points are
    //     kept: cloud-sync placeholders such as OneDrive are reparse
    //     points too and are legitimate media.) The Library Location root
    //     itself is always scanned - the user chose it explicitly.
    //   * An unreadable folder or file is counted and skipped, never fatal.
    //   * Nothing here ever puts a filesystem path into a message: the
    //     Errors list holds fixed-wording summaries with counts only.
    //   * Iterative (explicit stack) with a depth limit, so a very deep
    //     tree cannot overflow the stack.
    public class LocalMediaScanService
    {
        private const int MaxDepth = 40;
        private const int ProgressEvery = 25;

        public async Task<LocalMediaScanResult> ScanAsync(
            LibraryLocation location,
            IProgress<LocalMediaScanProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new LocalMediaScanResult();

            if (location == null || string.IsNullOrWhiteSpace(location.Path))
            {
                result.Errors.Add("No library location was provided.");
                return result;
            }

            string rootPath = location.Path;

            await Task.Run(() =>
            {
                try
                {
                    ScanRoot(rootPath, result, progress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is reported through WasCancelled below.
                }
                catch (Exception ex)
                {
                    // Unexpected failure: reported as a fixed, path-free
                    // summary; the exception type is kept for diagnosis.
                    result.Errors.Add("The folder could not be scanned (" + ex.GetType().Name + ").");
                    result.LocationAccessible = false;
                }
            }).ConfigureAwait(false);

            result.WasCancelled = cancellationToken.IsCancellationRequested;

            if (result.InaccessibleCount > 0)
            {
                result.Errors.Add(result.InaccessibleCount + " folder(s) or file(s) could not be read and were skipped.");
            }

            return result;
        }

        private void ScanRoot(
            string rootPath,
            LocalMediaScanResult result,
            IProgress<LocalMediaScanProgress> progress,
            CancellationToken cancellationToken)
        {
            DirectoryInfo root;

            try
            {
                root = new DirectoryInfo(rootPath);

                // A missing / disconnected drive or removed folder is a
                // real, expected condition: reported as inaccessible, not
                // as a failure.
                if (!root.Exists)
                {
                    result.LocationAccessible = false;
                    return;
                }
            }
            catch (Exception)
            {
                result.LocationAccessible = false;
                return;
            }

            string rootFull = TrimTrailingSeparators(root.FullName);
            result.LocationAccessible = true;

            var state = new LocalMediaScanProgress { Phase = "Scanning folders" };
            var pending = new Stack<KeyValuePair<DirectoryInfo, int>>();
            pending.Push(new KeyValuePair<DirectoryInfo, int>(root, 0));

            bool isRoot = true;

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                KeyValuePair<DirectoryInfo, int> current = pending.Pop();
                DirectoryInfo directory = current.Key;
                int depth = current.Value;

                try
                {
                    foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        FileAttributes attributes;

                        try
                        {
                            attributes = entry.Attributes;
                        }
                        catch (Exception)
                        {
                            result.InaccessibleCount++;
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                result.SkippedLinkCount++;
                                continue;
                            }

                            if (depth + 1 > MaxDepth)
                            {
                                result.InaccessibleCount++;
                                continue;
                            }

                            DirectoryInfo child = entry as DirectoryInfo;

                            if (child != null)
                            {
                                pending.Push(new KeyValuePair<DirectoryInfo, int>(child, depth + 1));
                            }

                            continue;
                        }

                        state.FilesScanned++;

                        if (LocalMediaIdentificationService.IsSupportedVideoFile(entry.Name))
                        {
                            AddVideoFile(entry, rootFull, result, state);
                        }

                        if (progress != null && state.FilesScanned % ProgressEvery == 0)
                        {
                            progress.Report(CopyProgress(state));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Permission denied, a drive disconnecting mid-scan, a
                    // path that is too long: counted, never thrown - one
                    // inaccessible folder never aborts the rest.
                    if (isRoot)
                    {
                        // The root itself could not be listed.
                        result.LocationAccessible = false;
                    }

                    result.InaccessibleCount++;
                }

                isRoot = false;
            }

            if (progress != null)
            {
                progress.Report(CopyProgress(state));
            }
        }

        private static void AddVideoFile(
            FileSystemInfo entry,
            string rootFull,
            LocalMediaScanResult result,
            LocalMediaScanProgress state)
        {
            string fullPath;
            long size;
            long lastWriteTicks;

            try
            {
                fullPath = entry.FullName;
                var fileInfo = entry as FileInfo;
                size = fileInfo != null ? fileInfo.Length : 0;
                lastWriteTicks = entry.LastWriteTimeUtc.Ticks;
            }
            catch (Exception)
            {
                result.InaccessibleCount++;
                return;
            }

            string relative = MakeRelative(rootFull, fullPath);

            if (LocalMediaIdentificationService.GetExclusionReason(relative, size) != null)
            {
                result.ExcludedCount++;
                return;
            }

            LocalMediaFile file = LocalMediaIdentificationService.Classify(fullPath, relative);

            if (file == null)
            {
                return;
            }

            file.LocationPath = rootFull;
            file.RelativePath = relative;
            file.SizeBytes = size;
            file.LastWriteUtcTicks = lastWriteTicks;

            result.Files.Add(file);
            state.VideoFilesFound++;
        }

        internal static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Keep a drive root such as "D:\" intact ("D:" would mean the
            // current directory of that drive).
            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed + Path.DirectorySeparatorChar;
            }

            return trimmed.Length == 0 ? path : trimmed;
        }

        internal static string MakeRelative(string rootFull, string fullPath)
        {
            if (string.IsNullOrEmpty(rootFull) || string.IsNullOrEmpty(fullPath))
            {
                return fullPath;
            }

            if (fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(rootFull.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return Path.GetFileName(fullPath);
        }

        private static LocalMediaScanProgress CopyProgress(LocalMediaScanProgress state)
        {
            return new LocalMediaScanProgress
            {
                FilesScanned = state.FilesScanned,
                VideoFilesFound = state.VideoFilesFound,
                Phase = state.Phase
            };
        }
    }
}
