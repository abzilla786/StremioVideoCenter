using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // LOCAL MEDIA MODELS
    // =============================================================
    //
    // Plain data models for the Local Files feature: Library Locations
    // (persisted in plugin settings, global), the result of scanning and
    // filename classification, and the persisted library index (stored in
    // its own file in the plugin data folder - never in settings).
    //
    // Pipeline: LocalLibraryService (which folders) -> LocalMediaScanService
    // (what files) -> LocalMediaIdentificationService (what the filename
    // says) -> LocalMediaMatchingService (which Cinemeta/Kitsu title) ->
    // LocalMediaIndexStore (persisted index) -> LocalLibraryManager
    // (orchestration, browsing queries, local playback source).

    // =========================================================
    // LIBRARY LOCATION
    // =========================================================
    //
    // One folder the user has added as a local media source. Persisted
    // as part of StremioVideoCenterSettings.LibraryLocations - reusing
    // Playnite's own existing plugin-settings persistence mechanism (the
    // same one AddonInstances already uses). Locations are global (not
    // profile-owned), matching the existing settings model.
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
    // Anime is not a distinct value: the "anime" signal is carried
    // separately by LocalMediaFile.LooksLikeAnime because the same
    // TvEpisode shape covers both; whether it is really anime is decided
    // by metadata matching (Kitsu), never by the filename alone.
    public enum LocalMediaKind
    {
        Unknown = 0,
        Movie = 1,
        TvEpisode = 2
    }

    // =========================================================
    // LOCAL MEDIA FILE (one scan/classification result)
    // =========================================================
    public class LocalMediaFile
    {
        // Absolute path of the file. In-memory only for the scan: it is
        // split into LocationPath + RelativePath for the index and is never
        // written to a log or shown in an error.
        public string FullPath { get; set; }

        // The Library Location root this file was found under, and the
        // file's path relative to it (set by the scanner).
        public string LocationPath { get; set; }

        public string RelativePath { get; set; }

        public string FileName { get; set; }

        public long SizeBytes { get; set; }

        public long LastWriteUtcTicks { get; set; }

        public LocalMediaKind Kind { get; set; }

        // Movie: the parsed title. TvEpisode: the parsed show title.
        public string ParsedTitle { get; set; }

        // Movie: release year. TvEpisode: a year found in the show title
        // (e.g. "Show (2019) S01E02"), used only to disambiguate matching.
        public int? ParsedYear { get; set; }

        // TvEpisode only. Anime without a season ("[Group] Title - 05")
        // leaves ParsedSeason null and carries the episode number only.
        public int? ParsedSeason { get; set; }

        public int? ParsedEpisode { get; set; }

        // Multi-episode files ("S01E01E02", "S01E01-E03", "1x01-02"): the
        // last episode number covered. Null for a single-episode file.
        public int? ParsedEpisodeEnd { get; set; }

        // True for the fansub "[Group] Title - 05" convention.
        public bool LooksLikeAnime { get; set; }

        // The filename (or its folders) could be read more than one way and
        // the parser refused to guess. The item stays in the library as an
        // unmatched, flagged entry. AmbiguityReason is fixed wording.
        public bool IsAmbiguous { get; set; }

        public string AmbiguityReason { get; set; }

        // Always null from the scanner - matching happens later and is
        // stored on LocalLibraryItem. Kept for source compatibility.
        public string MatchedStremioId { get; set; }
    }

    // =========================================================
    // SCAN PROGRESS
    // =========================================================

    public class LocalMediaScanProgress
    {
        public int FilesScanned { get; set; }

        public int VideoFilesFound { get; set; }

        // Fixed-wording phase label ("Scanning folders", "Matching titles"
        // ...). Never a path.
        public string Phase { get; set; }

        // 1-based index of the Library Location being scanned and the total.
        public int LocationIndex { get; set; }

        public int LocationCount { get; set; }

        // Matching phase only.
        public int MatchesDone { get; set; }

        public int MatchesTotal { get; set; }
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

        // Redacted, fixed-wording summaries only (counts, never paths).
        public List<string> Errors { get; set; }

        public bool WasCancelled { get; set; }

        // False when the location itself could not be opened (missing,
        // disconnected drive, no permission). Used so the index never marks
        // a whole location's files as removed because it was unreachable.
        public bool LocationAccessible { get; set; }

        // Entries skipped because they were samples/trailers/extras.
        public int ExcludedCount { get; set; }

        // Directory junctions / symbolic links / reparse points not followed.
        public int SkippedLinkCount { get; set; }

        // Folders or files that could not be read.
        public int InaccessibleCount { get; set; }
    }

    // =========================================================
    // FOLDER STATE (Library Locations screen)
    // =========================================================

    public enum LocalFolderState
    {
        Accessible = 0,
        Missing = 1,
        Unavailable = 2
    }

    public class LocalFolderStatus
    {
        // The configured location path. Shown only in the Folders panel
        // (the user's own configuration); never logged or put in an error.
        public string Path { get; set; }

        public string DisplayName { get; set; }

        public LocalFolderState State { get; set; }

        public int ItemCount { get; set; }
    }

    // =========================================================
    // MATCH STATUS / CANDIDATES
    // =========================================================

    public enum LocalMatchStatus
    {
        // No usable match (yet). Playable; shown as unmatched.
        Unmatched = 0,

        // One clearly best candidate was accepted automatically.
        Matched = 1,

        // Several plausible candidates and none clearly best: NOT assigned.
        NeedsReview = 2,

        // The user picked the match.
        UserConfirmed = 3,

        // The user chose "keep unmatched": never auto-matched again.
        UserUnmatched = 4
    }

    public class LocalMatchCandidate
    {
        // "tt1234567" (Cinemeta) or "kitsu:123" (Kitsu).
        public string Id { get; set; }

        // "movie", "series" or "anime".
        public string Type { get; set; }

        public string Title { get; set; }

        public string Year { get; set; }

        public string Poster { get; set; }

        public double Score { get; set; }

        // Pure title similarity (0..1) behind Score, and alternate titles
        // (Kitsu English / romaji) used only while ranking. Not persisted.
        [JsonIgnore]
        public double TitleScore { get; set; }

        [JsonIgnore]
        public string[] AlternateTitles { get; set; }
    }

    // =========================================================
    // LOCAL LIBRARY ITEM (one persisted index entry)
    // =========================================================
    public class LocalLibraryItem
    {
        public LocalLibraryItem()
        {
            Candidates = new List<LocalMatchCandidate>();
        }

        // Stable, path-free identity ("local-" + 16 hex). Assigned when the
        // file is first seen and then carried forward - see
        // LocalMediaIndexStore.NewItemId and LocalLibraryManager's
        // reconciliation notes (rename / move handling).
        public string Id { get; set; }

        public string LocationPath { get; set; }

        public string RelativePath { get; set; }

        public string FileName { get; set; }

        public long SizeBytes { get; set; }

        public long LastWriteUtcTicks { get; set; }

        public LocalMediaKind Kind { get; set; }

        public string ParsedTitle { get; set; }

        public int? ParsedYear { get; set; }

        public int? ParsedSeason { get; set; }

        public int? ParsedEpisode { get; set; }

        public int? ParsedEpisodeEnd { get; set; }

        public bool LooksLikeAnime { get; set; }

        public bool IsAmbiguous { get; set; }

        public string AmbiguityReason { get; set; }

        // Another item (this id) already represents the same matched
        // episode/movie; this one is a duplicate copy.
        public string DuplicateOfId { get; set; }

        public LocalMatchStatus MatchStatus { get; set; }

        // "tt…" or "kitsu:…" of the matched movie / SHOW.
        public string MatchedId { get; set; }

        // "movie", "series" or "anime".
        public string MatchedType { get; set; }

        public string MatchedTitle { get; set; }

        public string MatchedYear { get; set; }

        public string PosterUrl { get; set; }

        public string Overview { get; set; }

        // Numbering used to build the canonical key for a matched episode
        // (Cinemeta: season + episode; Kitsu: episode only, season null).
        public int? MatchedSeason { get; set; }

        public int? MatchedEpisode { get; set; }

        public List<LocalMatchCandidate> Candidates { get; set; }

        // Normalised "title|year|kind" the current match was computed from;
        // a different value means the filename now reads differently and the
        // match must be recomputed (unless the user set it).
        public string MatchQueryKey { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        // The file was not found by the last scan of a reachable location.
        // Kept in the index so a moved / renamed file can re-attach to it.
        public bool IsMissing { get; set; }

        // Shallow copy (the candidate list is copied) used for snapshots so
        // the UI never reads an item a background scan is updating.
        public LocalLibraryItem Clone()
        {
            var copy = (LocalLibraryItem)MemberwiseClone();
            copy.Candidates = Candidates != null
                ? new List<LocalMatchCandidate>(Candidates)
                : new List<LocalMatchCandidate>();
            return copy;
        }

        [JsonIgnore]
        public string FullPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(LocationPath) || string.IsNullOrWhiteSpace(RelativePath))
                {
                    return null;
                }

                return Path.Combine(LocationPath, RelativePath);
            }
        }

        [JsonIgnore]
        public bool IsMatched
        {
            get
            {
                return (MatchStatus == LocalMatchStatus.Matched ||
                        MatchStatus == LocalMatchStatus.UserConfirmed) &&
                       !string.IsNullOrWhiteSpace(MatchedId);
            }
        }

        [JsonIgnore]
        public bool IsKitsu
        {
            get
            {
                return IsMatched &&
                       MatchedId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase);
            }
        }

        [JsonIgnore]
        public bool IsEpisode
        {
            get { return Kind == LocalMediaKind.TvEpisode; }
        }

        // The Watch State canonical key this item plays under - exactly the
        // shape ContentRequest.BuildStremioId() produces for the same
        // content, so a streaming copy and a local copy share progress:
        //   movie    "tt1234567"
        //   episode  "tt1234567:2:5"
        //   Kitsu    "kitsu:12345:5"
        // An unmatched item (or a matched episode with unknown numbering)
        // plays under its own path-free id.
        [JsonIgnore]
        public string CanonicalKey
        {
            get
            {
                if (!IsMatched)
                {
                    return Id;
                }

                if (IsEpisode)
                {
                    if (IsKitsu)
                    {
                        return MatchedEpisode.HasValue ? MatchedId + ":" + MatchedEpisode.Value : Id;
                    }

                    return (MatchedSeason.HasValue && MatchedEpisode.HasValue)
                        ? MatchedId + ":" + MatchedSeason.Value + ":" + MatchedEpisode.Value
                        : Id;
                }

                // Matched as a movie.
                return string.Equals(MatchedType, "movie", StringComparison.OrdinalIgnoreCase) ? MatchedId : Id;
            }
        }
    }

    // =========================================================
    // LIBRARY INDEX (the persisted file)
    // =========================================================
    public class LocalLibraryIndex
    {
        public const int CurrentVersion = 1;

        public LocalLibraryIndex()
        {
            Version = CurrentVersion;
            Items = new List<LocalLibraryItem>();
        }

        public int Version { get; set; }

        public DateTime LastScanUtc { get; set; }

        public List<LocalLibraryItem> Items { get; set; }
    }

    // =========================================================
    // LIBRARY STATUS (manager -> view)
    // =========================================================

    public enum LocalScanPhase
    {
        Idle = 0,
        Scanning = 1,
        Matching = 2
    }

    public class LocalLibraryStatus
    {
        public LocalScanPhase Phase { get; set; }

        public LocalMediaScanProgress Progress { get; set; }

        // Fixed-wording summary of the last completed / cancelled / failed
        // scan, or null. Never contains a path.
        public string LastMessage { get; set; }

        public bool LastScanFailed { get; set; }
    }
}
