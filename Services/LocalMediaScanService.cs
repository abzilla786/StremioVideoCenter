using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA SCAN SERVICE (Slice 15 - architecture foundation)
    // =============================================================
    //
    // Filesystem discovery only - recursively finds supported video
    // files under one LibraryLocation and hands each one to
    // LocalMediaIdentificationService for filename-based classification.
    // Separate from LocalLibraryService (which owns WHICH locations
    // exist) and from LocalMediaIdentificationService (which owns HOW a
    // filename is parsed) - three distinct concerns, per this slice's
    // own "separate filesystem discovery / media identification /
    // metadata enrichment / library persistence / playback" instruction.
    //
    // Never invoked automatically - no background scanning exists this
    // slice (explicitly out of scope). Callable directly (proven via
    // this class's own shape - cancellable, progress-reporting, one
    // location at a time) so a future slice can wire it to a real
    // scan-progress UI or a background scheduler without redesigning
    // this layer.
    public class LocalMediaScanService
    {
        public async Task<LocalMediaScanResult> ScanAsync(
            LibraryLocation location,
            IProgress<LocalMediaScanProgress> progress,
            CancellationToken cancellationToken)
        {
            var result = new LocalMediaScanResult();

            if (location == null || string.IsNullOrWhiteSpace(location.Path))
            {
                result.Errors.Add("No library location provided.");
                return result;
            }

            if (!Directory.Exists(location.Path))
            {
                // A missing/disconnected drive or a removed folder is a
                // real, expected condition (see this slice's own
                // "missing/disconnected drives" consideration) - reported
                // as an honest per-location error, never a crash.
                result.Errors.Add("Location is not currently accessible: " + location.Path);
                return result;
            }

            var progressState = new LocalMediaScanProgress();

            await Task.Run(() =>
            {
                ScanDirectory(location.Path, result, progressState, progress, cancellationToken);
            });

            result.WasCancelled = cancellationToken.IsCancellationRequested;

            return result;
        }

        private void ScanDirectory(
            string directoryPath,
            LocalMediaScanResult result,
            LocalMediaScanProgress progressState,
            IProgress<LocalMediaScanProgress> progress,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries(directoryPath);
            }
            catch (Exception ex)
            {
                // Permission denied, a drive disconnecting mid-scan, etc. -
                // recorded, not thrown - one inaccessible subfolder never
                // aborts the rest of the scan.
                result.Errors.Add(directoryPath + ": " + ex.Message);
                return;
            }

            foreach (string entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (Directory.Exists(entry))
                {
                    ScanDirectory(entry, result, progressState, progress, cancellationToken);
                    continue;
                }

                progressState.FilesScanned++;
                progressState.CurrentPath = entry;

                if (LocalMediaIdentificationService.IsSupportedVideoFile(entry))
                {
                    LocalMediaFile file = LocalMediaIdentificationService.Classify(entry);

                    try
                    {
                        file.SizeBytes = new FileInfo(entry).Length;
                    }
                    catch
                    {
                        // Size is best-effort metadata only.
                    }

                    result.Files.Add(file);
                    progressState.VideoFilesFound++;
                }

                if (progress != null && progressState.FilesScanned % 25 == 0)
                {
                    progress.Report(new LocalMediaScanProgress
                    {
                        FilesScanned = progressState.FilesScanned,
                        VideoFilesFound = progressState.VideoFilesFound,
                        CurrentPath = progressState.CurrentPath
                    });
                }
            }
        }
    }
}
