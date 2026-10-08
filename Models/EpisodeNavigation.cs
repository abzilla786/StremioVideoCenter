using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StremioVideoCenter.Models
{
    // One episode of a series, as far as navigation is concerned. Identity is
    // the SAME canonical key Watch State already uses:
    //   TV:    "tt1234567:<season>:<episode>"
    //   Kitsu: "kitsu:<id>:<absoluteEpisodeNumber>"
    // No new identity scheme is introduced here.
    public sealed class EpisodeRef
    {
        public string CanonicalKey { get; set; }

        // Show-level id: "tt…" or "kitsu:<id>".
        public string ShowStremioId { get; set; }

        // Display/ordering season. For Kitsu this is only a display grouping
        // (Kitsu identity is the absolute episode number alone).
        public int Season { get; set; }

        public int Episode { get; set; }

        public string Title { get; set; }

        public bool IsKitsu { get; set; }
    }

    // The ordered episodes of one series, with exact previous/next lookup.
    //
    // Ordering: TV by (season, episode); Kitsu by absolute episode number.
    // Season 0 ("Specials") is its own sequence: navigating from a regular
    // episode never wanders into specials (S01E01's previous is nothing, not
    // the last special) and a special only navigates among specials.
    public sealed class EpisodeList
    {
        private readonly List<EpisodeRef> _regular;
        private readonly List<EpisodeRef> _specials;

        public string ShowStremioId { get; private set; }

        private EpisodeList(string showStremioId, List<EpisodeRef> regular, List<EpisodeRef> specials)
        {
            ShowStremioId = showStremioId;
            _regular = regular;
            _specials = specials;
        }

        public static EpisodeList Create(string showStremioId, IEnumerable<EpisodeRef> episodes)
        {
            List<EpisodeRef> unique = new List<EpisodeRef>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (EpisodeRef episode in episodes ?? Enumerable.Empty<EpisodeRef>())
            {
                if (episode == null || string.IsNullOrWhiteSpace(episode.CanonicalKey) || !seen.Add(episode.CanonicalKey))
                {
                    continue;
                }

                unique.Add(episode);
            }

            List<EpisodeRef> specials = unique
                .Where(e => !e.IsKitsu && e.Season <= 0)
                .OrderBy(e => e.Episode)
                .ToList();

            List<EpisodeRef> regular = unique
                .Where(e => e.IsKitsu || e.Season > 0)
                .OrderBy(e => e.IsKitsu ? 0 : e.Season)
                .ThenBy(e => e.Episode)
                .ToList();

            return new EpisodeList(showStremioId, regular, specials);
        }

        public int Count
        {
            get { return _regular.Count + _specials.Count; }
        }

        // True only for a parseable date clearly in the future (a day of slack
        // for time zones). Unknown/unparseable dates are kept as playable.
        public static bool IsFutureAirDate(string airDate, DateTime utcNow)
        {
            DateTime parsed;

            if (!string.IsNullOrWhiteSpace(airDate) &&
                DateTime.TryParse(
                    airDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out parsed))
            {
                return parsed > utcNow.AddDays(1);
            }

            return false;
        }

        public EpisodeRef Find(string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return null;
            }

            return _regular.Concat(_specials).FirstOrDefault(e =>
                string.Equals(e.CanonicalKey, canonicalKey, StringComparison.OrdinalIgnoreCase));
        }

        public EpisodeRef Previous(string canonicalKey)
        {
            return Neighbour(canonicalKey, -1);
        }

        public EpisodeRef Next(string canonicalKey)
        {
            return Neighbour(canonicalKey, 1);
        }

        // 1-based position within the episode's own sequence, or 0 if unknown.
        public int PositionOf(string canonicalKey)
        {
            List<EpisodeRef> sequence = SequenceOf(canonicalKey);
            return sequence == null ? 0 : IndexIn(sequence, canonicalKey) + 1;
        }

        public int TotalFor(string canonicalKey)
        {
            List<EpisodeRef> sequence = SequenceOf(canonicalKey);
            return sequence == null ? 0 : sequence.Count;
        }

        private EpisodeRef Neighbour(string canonicalKey, int direction)
        {
            List<EpisodeRef> sequence = SequenceOf(canonicalKey);
            if (sequence == null)
            {
                return null;
            }

            int index = IndexIn(sequence, canonicalKey) + direction;
            return index >= 0 && index < sequence.Count ? sequence[index] : null;
        }

        private List<EpisodeRef> SequenceOf(string canonicalKey)
        {
            if (IndexIn(_regular, canonicalKey) >= 0) return _regular;
            if (IndexIn(_specials, canonicalKey) >= 0) return _specials;
            return null;
        }

        private static int IndexIn(List<EpisodeRef> sequence, string canonicalKey)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return -1;
            }

            for (int i = 0; i < sequence.Count; i++)
            {
                if (string.Equals(sequence[i].CanonicalKey, canonicalKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    // What the player needs to know about the episode it is playing: the
    // series, the exact episode, and its neighbours. Attached to the
    // PlaybackContext once the episode list is known; absent for movies.
    public sealed class EpisodeContext
    {
        public EpisodeList List { get; private set; }

        public EpisodeRef Current { get; private set; }

        public string SeriesTitle { get; private set; }

        public EpisodeRef Previous { get; private set; }

        public EpisodeRef Next { get; private set; }

        public int Position { get; private set; }

        public int Total { get; private set; }

        public bool HasPrevious { get { return Previous != null; } }

        public bool HasNext { get { return Next != null; } }

        private EpisodeContext() { }

        // Null if the current key is not in the list (never guess a position).
        public static EpisodeContext Create(EpisodeList list, string currentCanonicalKey, string mediaItemTitle)
        {
            if (list == null)
            {
                return null;
            }

            EpisodeRef current = list.Find(currentCanonicalKey);
            if (current == null)
            {
                return null;
            }

            return new EpisodeContext
            {
                List = list,
                Current = current,
                SeriesTitle = DeriveSeriesTitle(mediaItemTitle, current.Title),
                Previous = list.Previous(current.CanonicalKey),
                Next = list.Next(current.CanonicalKey),
                Position = list.PositionOf(current.CanonicalKey),
                Total = list.TotalFor(current.CanonicalKey)
            };
        }

        // Episode MediaItems carry "<Series> - <Episode name>" as their title
        // (see EpisodeBrowserView.BuildEpisodeMediaItem); strip that suffix to
        // recover the series title.
        public static string DeriveSeriesTitle(string mediaItemTitle, string episodeTitle)
        {
            if (string.IsNullOrWhiteSpace(mediaItemTitle))
            {
                return mediaItemTitle;
            }

            if (!string.IsNullOrWhiteSpace(episodeTitle))
            {
                string suffix = " - " + episodeTitle;
                if (mediaItemTitle.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return mediaItemTitle.Substring(0, mediaItemTitle.Length - suffix.Length);
                }
            }

            int fallback = mediaItemTitle.LastIndexOf(" - S", StringComparison.Ordinal);
            return fallback > 0 ? mediaItemTitle.Substring(0, fallback) : mediaItemTitle;
        }

        // "S03E04", or "Episode 12" for Kitsu's absolute numbering.
        public static string FormatCode(EpisodeRef episode)
        {
            if (episode == null)
            {
                return string.Empty;
            }

            return episode.IsKitsu
                ? "Episode " + episode.Episode
                : "S" + episode.Season.ToString("00") + "E" + episode.Episode.ToString("00");
        }

        // "S03E04 — Episode Title" (title omitted when unknown).
        public static string FormatLabel(EpisodeRef episode)
        {
            if (episode == null)
            {
                return string.Empty;
            }

            string code = FormatCode(episode);
            return string.IsNullOrWhiteSpace(episode.Title) ? code : code + " — " + episode.Title;
        }

        // The playback MediaItem for navigating to another episode of the
        // same series, in the same shape the Episode Browser produces
        // (show-level StremioId + Season/Episode, "<Series> - <Name>" title).
        public MediaItem BuildMediaItem(EpisodeRef target, MediaItem current)
        {
            return new MediaItem
            {
                StremioId = target.ShowStremioId,
                Season = target.Season,
                Episode = target.Episode,
                Type = current != null ? current.Type : null,
                Genres = current != null && current.Genres != null ? new List<string>(current.Genres) : null,
                Title = (SeriesTitle ?? string.Empty) +
                    (!string.IsNullOrWhiteSpace(target.Title)
                        ? " - " + target.Title
                        : " - S" + target.Season + "E" + target.Episode),
                Subtitle = "Season " + target.Season + " · Episode " + target.Episode,
                PosterPath = current != null ? current.PosterPath : null,
                Year = current != null ? current.Year : null
            };
        }
    }
}
