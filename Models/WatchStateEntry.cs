using System;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // WATCH STATE ENTRY (Slice 16B - Persistent Profile-Owned Watch State)
    // =============================================================
    //
    // One profile-owned playback-progress record for one piece of
    // content. This is NOT Continue Watching, History, or My List -
    // those are later, separate profile-owned data models/phases (Slice
    // 16C/16D/17) that will each be DERIVED from this data (see the
    // Slice 16 architecture audit's "Watch State -> incomplete entries
    // -> Continue Watching" note) rather than duplicating it.
    //
    // Uniqueness is ProfileId + CanonicalKey (enforced by
    // WatchStateService, not here) - there must never be two entries
    // for the same profile watching the same piece of content.
    //
    // CanonicalKey is the authoritative content identity and is always
    // exactly what ContentRequest.BuildStremioId() already produces for
    // that same piece of content in PlaySelectedMediaAsync:
    //   Movie:        "tt1234567"
    //   TV episode:    "tt1234567:2:5"          (imdbId:season:episode)
    //   Kitsu anime:   "kitsu:12345:5"           (kitsu:<id>:<absoluteEpisode>)
    // No second identity scheme is introduced here - see WatchStateService
    // remarks for why this must never be recomputed independently.
    public class WatchStateEntry
    {
        public Guid ProfileId { get; set; }

        public ContentType ContentType { get; set; }

        // The authoritative identity - see class remarks above. Never
        // null/blank for a stored entry (WatchStateService refuses to
        // upsert one without it).
        public string CanonicalKey { get; set; }

        // The show/series-level Stremio id ("tt1234567" or "kitsu:12345")
        // for a TV/anime episode entry. Null for a movie entry, which has
        // no separate "show" concept - CanonicalKey alone already
        // identifies it completely.
        public string ShowStremioId { get; set; }

        // Display-only season/episode numbers. Null for a movie. For a
        // Kitsu-sourced anime episode, SeasonNumber is intentionally left
        // null (Kitsu's identity is the absolute episode number alone -
        // see class remarks and KitsuService's own notes; a season number
        // must never be synthesised or relied on for identity here).
        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public double PositionSeconds { get; set; }

        public double DurationSeconds { get; set; }

        public bool IsCompleted { get; set; }

        public DateTime LastPlayedUtc { get; set; }

        // =========================================================
        // DENORMALISED DISPLAY METADATA
        // =========================================================
        //
        // Copied from the MediaItem in play at the moment this entry was
        // created/updated, purely so a future Continue Watching/History
        // UI can render a row without a second metadata lookup. Never
        // used for identity - CanonicalKey alone is authoritative.

        public string Title { get; set; }

        public string PosterPath { get; set; }

        public string Year { get; set; }

        // MediaItem.Type's raw display string ("Movie"/"TV"/"Anime"), not
        // the ContentType enum - kept alongside ContentType above the
        // same way MediaItem itself carries both a Type string and a
        // structured ContentType is derived from it via GetContentType.
        public string Type { get; set; }

        // The last source that actually played (mpv confirmed the file
        // loaded) for this profile + content. Non-secret identifiers only
        // - never a stream URL or addon manifest URL. Null for entries
        // created before this existed or when no usable identifier was
        // available. See SavedSourceReference.
        public SavedSourceReference SavedSource { get; set; }
    }
}
