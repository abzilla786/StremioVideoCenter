namespace StremioVideoCenter.Models
{
    // =============================================================
    // EPISODE BROWSER DISPLAY MODELS
    // =============================================================
    //
    // Plain, provider-agnostic display shapes used by EpisodeBrowserView
    // for BOTH Cinemeta (Movie/TV) and Kitsu (Anime) episodes - the view's
    // UI-building code never needs to know which provider supplied the
    // data (they are display DTOs only).

    public class SeasonSummary
    {
        public int Id { get; set; }

        public int SeasonNumber { get; set; }

        public string Name { get; set; }

        public int EpisodeCount { get; set; }

        public string PosterPath { get; set; }

        public string AirDate { get; set; }
    }

    public class EpisodeDetails
    {
        public int Id { get; set; }

        public int EpisodeNumber { get; set; }

        public int SeasonNumber { get; set; }

        public string Name { get; set; }

        public string Overview { get; set; }

        // Absolute image URL (Cinemeta thumbnail / Kitsu episode thumbnail).
        public string StillPath { get; set; }

        public string AirDate { get; set; }

        public double VoteAverage { get; set; }

        public int? Runtime { get; set; }
    }
}
