using System;

namespace StremioVideoCenter.Models
{
    // A fully-identified request for "find me the Stremio streams for this
    // piece of content" - movie, TV episode, or anime episode. This is the
    // single input to StremioContentDiscoveryService.ResolveStreamsAsync;
    // everything upstream (catalogue lookups, season/episode selection) is
    // responsible for filling one of these in correctly.
    public class ContentRequest
    {
        public ContentType Type { get; set; }

        public string Title { get; set; }

        // The show/movie-level IMDb id, e.g. "tt1234567". Null when no
        // IMDb id is known for this title - a real and fairly common case
        // for some anime and less mainstream content, not an error
        // condition.
        public string ImdbId { get; set; }

        // Only meaningful when Type is TvShow or Anime. Null for Type.Movie.
        public int? Season { get; set; }

        public int? Episode { get; set; }

        public string EpisodeTitle { get; set; }

        // Escape hatch for content whose Stremio id doesn't follow the
        // standard "<imdbId>" / "<imdbId>:<season>:<episode>" shape - e.g. an
        // anime-specific addon that expects a Kitsu/MAL/AniList-style id
        // instead of IMDb. When set, this is used verbatim as the Stremio
        // content id instead of being derived from ImdbId/Season/Episode.
        // Nothing in this project currently populates this - it exists so a
        // future anime-specific metadata source has somewhere to put its id
        // without requiring another redesign of this pipeline. Deliberately
        // NOT wired to any specific provider today, per "avoid hard-coding a
        // specific anime provider or addon unless it is already present in
        // the project."
        public string ExplicitStremioId { get; set; }

        // The Stremio protocol's own "type" parameter for GetStreamsAsync.
        // Stremio only recognises "movie" and "series" - there is no "anime"
        // type in the protocol itself, so ContentType.Anime maps to
        // "series" here, the same as ContentType.TvShow.
        public string StremioType
        {
            get
            {
                switch (Type)
                {
                    case ContentType.Movie:
                        return "movie";

                    case ContentType.TvShow:
                    case ContentType.Anime:
                        return "series";

                    default:
                        return "movie";
                }
            }
        }

        // Builds the actual Stremio content id string - e.g. "tt1234567" for
        // a movie, or "tt1234567:1:2" for season 1 episode 2. Returns null
        // when there isn't enough information to build a valid id (no
        // ExplicitStremioId and no ImdbId, or a TV/anime request missing
        // season/episode). Callers must treat null as "cannot be resolved
        // with what we currently know" and report it as such, never attempt
        // a best-effort guess.
        public string BuildStremioId()
        {
            if (!string.IsNullOrWhiteSpace(ExplicitStremioId))
            {
                return ExplicitStremioId;
            }

            if (string.IsNullOrWhiteSpace(ImdbId))
            {
                return null;
            }

            if (Type == ContentType.Movie)
            {
                return ImdbId;
            }

            // TvShow or Anime: the show's IMDb id alone does not identify an
            // individual episode - season and episode must both be present.
            if (!Season.HasValue || !Episode.HasValue)
            {
                return null;
            }

            return ImdbId + ":" + Season.Value + ":" + Episode.Value;
        }
    }
}
