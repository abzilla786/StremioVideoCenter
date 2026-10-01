using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // WATCH HISTORY ENTRY (Slice 16C - Continue Watching + History)
    // =============================================================
    //
    // "This profile has interacted with/watched this content" - distinct
    // from WatchStateEntry ("where exactly did this profile leave
    // playback?"). The two are deliberately never merged (Slice 16C
    // spec, Section 11/18):
    //
    //   Movie:  one WatchHistoryEntry  <-> one WatchStateEntry (both
    //           keyed by the same movie canonical key).
    //   TV/Kitsu anime: one SERIES-LEVEL WatchHistoryEntry can correspond
    //           to MANY episode-level WatchStateEntry records - watching
    //           S01E01 then S01E02 updates the SAME History entry while
    //           creating a SECOND, separate Watch State entry.
    //
    // CanonicalKey here is always the SHOW/movie-level identity - in
    // this codebase that is simply MediaItem.StremioId (a movie's own id,
    // or a TV/Kitsu show's id with no season/episode/absolute-episode
    // suffix baked in - see EpisodeBrowserView.BuildEpisodeMediaItem's
    // own remarks: StremioId is already the show-level id, Season/Episode
    // are carried separately). No new identity scheme is introduced.
    public class WatchHistoryEntry
    {
        public Guid ProfileId { get; set; }

        public ContentType ContentType { get; set; }

        // Show/movie-level identity - see class remarks. Uniqueness key
        // is ProfileId + CanonicalKey (enforced by WatchHistoryService).
        public string CanonicalKey { get; set; }

        // The exact episode-level (or, for a movie, content-level) Watch
        // State CanonicalKey most recently watched under this History
        // entry - e.g. "tt1234567:2:5" or "kitsu:12345:5", or for a
        // movie, identical to CanonicalKey above. This is what makes
        // "History -> lookup Watch State" (Slice 16C spec, Section 17)
        // possible: WatchStateService.GetEntry(profileId,
        // LatestWatchedCanonicalKey) finds the associated progress, if
        // any, without EVER inferring it from Season+1/Episode+1.
        public string LatestWatchedCanonicalKey { get; set; }

        // Informational only - "the stored latest watched information is
        // informational; actual episode resolution should use the
        // existing content architecture" (Slice 16C spec, Section 28).
        // Null for a movie. Never used to compute a "next episode".
        public int? LatestSeasonNumber { get; set; }

        public int? LatestEpisodeNumber { get; set; }

        public DateTime LastWatchedUtc { get; set; }

        // =========================================================
        // DENORMALISED DISPLAY METADATA (mirrors WatchStateEntry)
        // =========================================================

        public string Title { get; set; }

        public string PosterPath { get; set; }

        public string Year { get; set; }

        public string Type { get; set; }

        // Slice 17C: series/movie-level genres for personalisation, copied
        // from the MediaItem in play when this entry is recorded. Null for
        // entries saved before 17C (no backfill) - treated as "no genre
        // signal". Never overwritten with an empty value.
        public List<string> Genres { get; set; }
    }
}
