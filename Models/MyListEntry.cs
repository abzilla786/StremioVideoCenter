using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // MY LIST ENTRY (Slice 17A - My List Core)
    // =============================================================
    //
    // "This profile has saved this content to My List" - a profile-owned,
    // content-level bookmark, deliberately separate from WatchStateEntry
    // (playback progress) and WatchHistoryEntry (watch activity). A user
    // adding/removing something from My List has nothing to do with
    // whether they have watched it.
    //
    // CanonicalKey here is always the SHOW/movie-level identity, exactly
    // the same convention WatchHistoryEntry.CanonicalKey already
    // establishes - in this codebase that is simply MediaItem.StremioId
    // (a movie's own id, or a TV/Kitsu show's id with no season/episode
    // suffix - StremioId already means "the show's id" even for an
    // episode-shaped MediaItem; see MediaItem.Season/.Episode remarks).
    // No new identity scheme is introduced, and an episode/season must
    // NEVER produce its own separate My List entry - adding to My List
    // from any episode of a show, or from the show itself, resolves to
    // the same single entry.
    //
    // Uniqueness key is ProfileId + CanonicalKey (enforced by
    // MyListService), the same rule WatchStateEntry/WatchHistoryEntry
    // already use.
    public class MyListEntry
    {
        public Guid ProfileId { get; set; }

        public ContentType ContentType { get; set; }

        // Show/movie-level identity - see class remarks.
        public string CanonicalKey { get; set; }

        // =========================================================
        // DENORMALISED DISPLAY METADATA (mirrors WatchStateEntry/
        // WatchHistoryEntry)
        // =========================================================
        //
        // Copied from the MediaItem in play at the moment this entry was
        // added, so My List can render (including filtering by genre)
        // without any network request.

        public string Title { get; set; }

        public string PosterPath { get; set; }

        public string Year { get; set; }

        public string Type { get; set; }

        public List<string> Genres { get; set; }

        public DateTime AddedUtc { get; set; }
    }
}
