namespace StremioVideoCenter.Models
{
    // How a PlaybackSource became playable. Only two values, deliberately:
    // an "Unresolved" source never becomes a PlaybackSource at all (see
    // PlaybackSourceAggregationService/UnresolvedSource) - if something has
    // a PlaybackSourceKind, it is already playable.
    public enum PlaybackSourceKind
    {
        // StremioStream.Url was already a direct HTTP(S) URL - no
        // Real-Debrid involved.
        Direct,

        // StremioStream.InfoHash was resolved into a direct URL via
        // RealDebridService.
        RealDebrid
    }
}
