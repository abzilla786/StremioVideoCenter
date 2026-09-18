namespace StremioVideoCenter.Models
{
    // App-level content classification, used throughout the discovery
    // pipeline instead of scattering "movie"/"series"/"anime" string
    // literals. This is NOT the same as the Stremio protocol's own "type"
    // parameter - Stremio only recognises "movie" and "series" at the
    // protocol level. See ContentRequest.StremioType for that mapping.
    public enum ContentType
    {
        Movie,

        TvShow,

        // Episodic (series-shaped) anime specifically - the overwhelmingly
        // common case, and consistent with how this project already sources
        // anime candidates (TmdbService.GetAnimeCandidatesAsync queries
        // TMDB's "tv" endpoint filtered by genre/origin, not a separate
        // TMDB content type). A standalone anime film has no episodes and
        // behaves exactly like an ordinary movie, so it belongs under
        // ContentType.Movie, not here.
        Anime
    }
}
