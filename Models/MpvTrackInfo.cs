namespace StremioVideoCenter.Models
{
    // One entry from mpv's "track-list" property. Plain data: parsed from
    // mpv's JSON by MpvTrackParser, never constructed by hand from UI code.
    // Track ids are local to the CURRENTLY LOADED file: a new file (next
    // episode, replacement source) has its own tracks and ids.
    public sealed class MpvTrackInfo
    {
        public int Id { get; set; }

        // "audio" or "sub" (mpv's own type strings).
        public string Type { get; set; }

        public string Language { get; set; }
        public string Title { get; set; }
        public string Codec { get; set; }

        // mpv's demux-channel-count; 0 when mpv did not report it.
        public int ChannelCount { get; set; }

        public bool IsSelected { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        public bool IsExternal { get; set; }

        // Kept for compatibility; menus use TrackLabels.
        public string DisplayName
        {
            get { return TrackLabels.Describe(this, false); }
        }
    }
}
