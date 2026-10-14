namespace StremioVideoCenter.Models
{
    // How a PlaybackSource became playable. An "Unresolved" source never
    // becomes a PlaybackSource at all (see PlaybackSourceAggregationService/
    // UnresolvedSource) - if something has a PlaybackSourceKind, it is
    // already playable.
    public enum PlaybackSourceKind
    {
        // StremioStream.Url was already a direct HTTP(S) URL - no
        // Real-Debrid involved.
        Direct,

        // StremioStream.InfoHash was resolved into a direct URL via
        // RealDebridService.
        RealDebrid,

        // Local Files: PlaybackSource.Url is an absolute filesystem path on
        // this machine (handed to mpv as-is, not a URL). Appended AFTER the
        // two existing values so persisted numeric values are unchanged.
        // Only LocalLibraryManager ever produces this kind - the streaming
        // aggregator and resolver never do - and it is never mixed up with a
        // streaming source (SavedSourceReference keeps the two apart).
        Local
    }
}
