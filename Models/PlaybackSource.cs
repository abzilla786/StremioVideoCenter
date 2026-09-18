using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // The player-facing output of StreamResolverService. MpvPlayer/PlayerSession
    // never see a StremioStream or know Real-Debrid exists - by the time
    // playback code touches anything, it's already this.
    //
    // Headers exists for future streams that need custom request headers to
    // play (e.g. some Real-Debrid links, some addon sources). Nothing in this
    // phase reads it - PlayVideoAsync(string url) still only takes the URL.
    // It's here so PlaybackSource doesn't need a breaking shape change the
    // day something actually requires headers.
    public class PlaybackSource
    {
        public string Url { get; set; }

        public string Title { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        // =========================================================
        // SLICE 4 ADDITIONS - all additive, nothing above changed
        // =========================================================

        // The originating stream's own Name (often carries free-text
        // quality/release info, e.g. "1080p WEB-DL x264") - carried through
        // as-is. Deliberately not parsed into a structured Quality field
        // this slice: Stremio has no structured quality field to begin with,
        // and building a text-parsing heuristic isn't needed to prove the
        // aggregation backend works - a future source-selection UI can
        // decide how it wants to interpret/display this text.
        public string Name { get; set; }

        // Which configured addon produced the underlying stream, carried
        // over from StremioStream.SourceAddonUrl for display purposes once
        // a source-selection UI exists.
        public string SourceAddonUrl { get; set; }

        // Direct URL vs Real-Debrid-resolved - see PlaybackSourceKind.
        // Unresolved/unsupported streams never become a PlaybackSource at
        // all (see PlaybackSourceAggregationService/UnresolvedSource), so
        // there's no "Unresolved" value here - if you have one of these, it
        // is playable.
        public PlaybackSourceKind Kind { get; set; }
    }
}

