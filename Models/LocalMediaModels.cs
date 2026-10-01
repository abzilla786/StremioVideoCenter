using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // LOCAL MEDIA MODELS (Slice 15 - architecture foundation only)
    // =============================================================
    //
    // Plain data models for the Local Media / File Manager architecture
    // requested by Slice 15. Deliberately lightweight - a handful of
    // POCOs plus LocalLibraryService/LocalMediaScanService/
    // LocalMediaIdentificationService (see Services/) - not a database
    // system. This slice establishes the pipeline shape (Library
    // Locations -> folder/file discovery -> filename-based
    // identification) and makes it independently callable/testable;
    // it deliberately does NOT wire a full browsing UI, automatic
    // background scanning, or metadata matching against Cinemeta/IMDb -
    // those remain for a dedicated future Local Media slice (see
    // LocalMediaIdentificationService remarks for exactly where that
    // wiring point is). LocalMediaView itself stays free of any
    // scanning/indexing responsibility - it only ever displays counts
    // already computed by LocalLibraryService.

    // =========================================================
    // LIBRARY LOCATION
    // =========================================================
    //
    // One folder the user has added as a local media source. Persisted
    // as part of StremioVideoCenterSettings.LibraryLocations - reusing
    // Playnite's own existing plugin-settings persistence mechanism (the
    // same one AddonInstances already uses) rather than inventing a new
    // file/database, per "preserve existing configuration mechanisms"
    // and "do not build a giant database system prematurely."
    public class LibraryLocation
    {
        public string Path { get; set; }

        // User-facing label. Falls back to the folder name (Path's last
        // segment) when not explicitly set.
        public string DisplayName { get; set; }

        public DateTime AddedUtc { get; set; }
    }

    // =========================================================
    // LOCAL MEDIA KIND
    // =========================================================
    //
    // The result of filename-based classification only (see
    // LocalMediaIdentificationService) - never a confirmed identity.
    // Anime is deliberately NOT a distinct value here: disambiguating
    // anime from generic TV by filename pattern alone is unreliable
    // (the same "Title SxxEyy" shape covers both) - that distinction
    // requires real metadata matching (genre/origin), which is
    // explicitly out of scope for this architecture-only slice.
    public enum LocalMediaKind
    {
        Unknown = 0,
        Movie = 1,
        TvEpisode = 2
    }

    // =========================================================
    // LOCAL MEDIA FILE
    // =========================================================
    //
    // One discovered video file plus whatever LocalMediaIdentificationService
    // could confidently parse from its filename. MatchedStremioId is
    // always null this slice - no metadata-matching pass exists yet
    // (see LocalMediaIdentificationService remarks) - carried here only
    // so a future slice's metadata-matching pass has somewhere to put
    // its result without another model shape change, mirroring how
    // ContentRequest.ExplicitStremioId already documents itself as an
    // unused-today escape hatch for a similar reason.
    public class LocalMediaFile
    {
        public string FullPath { get; set; }

        public string FileName { get; set; }

        public long SizeBytes { get; set; }

        public LocalMediaKind Kind { get; set; }

        // Movie: the parsed title. TvEpisode: the parsed show title.
        public string ParsedTitle { get; set; }

        // Movie only.
        public int? ParsedYear { get; set; }

        // TvEpisode only.
        public int? ParsedSeason { get; set; }

        public int? ParsedEpisode { get; set; }

        // Always null this slice - see class remarks.
        public string MatchedStremioId { get; set; }
    }

    // =========================================================
    // SCAN PROGRESS
    // =========================================================

    public class LocalMediaScanProgress
    {
        public int FilesScanned { get; set; }

        public int VideoFilesFound { get; set; }

        public string CurrentPath { get; set; }
    }

    // =========================================================
    // SCAN RESULT
    // =========================================================

    public class LocalMediaScanResult
    {
        public LocalMediaScanResult()
        {
            Files = new List<LocalMediaFile>();
            Errors = new List<string>();
        }

        public List<LocalMediaFile> Files { get; set; }

        // Per-subfolder failures (permission denied, a disconnected
        // drive going away mid-scan, etc.) - collected rather than
        // aborting the whole scan, same "one failure doesn't take down
        // the batch" policy this codebase already follows elsewhere
        // (PlaybackSourceAggregationService, CatalogueService).
        public List<string> Errors { get; set; }

        public bool WasCancelled { get; set; }
    }
}
