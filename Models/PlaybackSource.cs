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
    }
}
