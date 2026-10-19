using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL LIBRARY BROWSE (pure view-model building)
    // =============================================================
    //
    // Turns index items into the entries the Local Files page shows:
    // movies / loose files, TV shows (episodes grouped per show), and the
    // episodes of one show. No I/O, no state, no UI types - the page maps
    // entries onto the existing MediaCard through MediaItem. Duplicates
    // (another copy of the same movie / episode) are folded into their
    // primary entry as a "copies" count; every copy stays playable.
    public enum LocalBrowseFilter
    {
        All = 0,
        Movies = 1,
        Tv = 2,
        Unmatched = 3
    }

    public enum LocalBrowseEntryKind
    {
        File = 0,
        Show = 1,
        Episode = 2
    }

    public sealed class LocalBrowseEntry
    {
        public LocalBrowseEntryKind Kind { get; set; }

        // Item id for a file / episode; "show:" + show key for a show.
        public string Key { get; set; }

        public string ShowKey { get; set; }

        public string Title { get; set; }

        // Small line under the title (year, "S01E05", "3 episodes").
        public string Detail { get; set; }

        // Status text for the card's third line ("Unmatched", "Check match",
        // "Offline", "2 copies") or empty.
        public string Badge { get; set; }

        public string PosterUrl { get; set; }

        // The primary item for a file / episode; null for a show.
        public LocalLibraryItem Item { get; set; }

        public int EpisodeCount { get; set; }

        public bool IsMatched { get; set; }
    }

    public static class LocalLibraryBrowse
    {
        // =========================================================
        // SHOW KEY
        // =========================================================
        //
        // A matched show is keyed by its Cinemeta / Kitsu id; an unmatched
        // one by a path-free hash of its normalised title (and year), so
        // its episodes still group together and keep a stable identity.
        public static string ShowKeyFor(LocalLibraryItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.IsMatched && item.IsEpisode)
            {
                return item.MatchedId;
            }

            string key = LocalMediaMatchingService.NormaliseTitle(item.ParsedTitle) +
                         "|" + (item.ParsedYear.HasValue ? item.ParsedYear.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);

            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var builder = new StringBuilder(LocalLibraryManager.ShowKeyPrefix, 10 + 12);

                for (int i = 0; i < 6; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        // =========================================================
        // TOP LEVEL
        // =========================================================
        public static List<LocalBrowseEntry> BuildTopLevel(
            IEnumerable<LocalLibraryItem> items,
            LocalBrowseFilter filter,
            string searchText,
            ISet<string> offlineLocations)
        {
            List<LocalLibraryItem> visible = Visible(items);
            Dictionary<string, int> copies = CountCopies(items);
            var entries = new List<LocalBrowseEntry>();

            foreach (LocalLibraryItem item in visible.Where(i => !i.IsEpisode))
            {
                if (filter == LocalBrowseFilter.Tv ||
                    (filter == LocalBrowseFilter.Unmatched && item.IsMatched))
                {
                    continue;
                }

                entries.Add(BuildFileEntry(item, LocalBrowseEntryKind.File, copies, offlineLocations));
            }

            if (filter != LocalBrowseFilter.Movies)
            {
                foreach (IGrouping<string, LocalLibraryItem> show in visible
                    .Where(i => i.IsEpisode)
                    .GroupBy(ShowKeyFor, StringComparer.OrdinalIgnoreCase))
                {
                    List<LocalLibraryItem> episodes = show.ToList();
                    LocalLibraryItem lead = episodes.FirstOrDefault(e => e.IsMatched) ?? episodes[0];
                    bool matched = lead.IsMatched;

                    if (filter == LocalBrowseFilter.Unmatched && matched)
                    {
                        continue;
                    }

                    entries.Add(new LocalBrowseEntry
                    {
                        Kind = LocalBrowseEntryKind.Show,
                        Key = "show:" + show.Key,
                        ShowKey = show.Key,
                        Title = matched ? lead.MatchedTitle : lead.ParsedTitle,
                        Detail = episodes.Count + (episodes.Count == 1 ? " episode" : " episodes"),
                        Badge = matched ? string.Empty : (lead.MatchStatus == LocalMatchStatus.NeedsReview ? "Check match" : "Unmatched"),
                        PosterUrl = episodes.Select(e => e.PosterUrl).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                        EpisodeCount = episodes.Count,
                        IsMatched = matched
                    });
                }
            }

            return Sort(Search(entries, searchText));
        }

        // =========================================================
        // EPISODES OF ONE SHOW
        // =========================================================
        public static List<LocalBrowseEntry> BuildEpisodes(
            IEnumerable<LocalLibraryItem> items,
            string showKey,
            string searchText,
            ISet<string> offlineLocations)
        {
            Dictionary<string, int> copies = CountCopies(items);

            List<LocalBrowseEntry> entries = Visible(items)
                .Where(i => i.IsEpisode && string.Equals(ShowKeyFor(i), showKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => EffectiveSeason(i))
                .ThenBy(i => i.ParsedEpisode ?? int.MaxValue)
                .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(i => BuildFileEntry(i, LocalBrowseEntryKind.Episode, copies, offlineLocations))
                .ToList();

            return Search(entries, searchText);
        }

        // =========================================================
        // HELPERS
        // =========================================================

        // Items that belong in the grid: present in the index, not a
        // duplicate copy (copies fold into their primary).
        private static List<LocalLibraryItem> Visible(IEnumerable<LocalLibraryItem> items)
        {
            return (items ?? Enumerable.Empty<LocalLibraryItem>())
                .Where(i => i != null && !i.IsMissing && string.IsNullOrEmpty(i.DuplicateOfId))
                .ToList();
        }

        private static Dictionary<string, int> CountCopies(IEnumerable<LocalLibraryItem> items)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (LocalLibraryItem item in items ?? Enumerable.Empty<LocalLibraryItem>())
            {
                if (item == null || item.IsMissing || string.IsNullOrEmpty(item.DuplicateOfId))
                {
                    continue;
                }

                int count;
                counts[item.DuplicateOfId] = counts.TryGetValue(item.DuplicateOfId, out count) ? count + 1 : 1;
            }

            return counts;
        }

        private static LocalBrowseEntry BuildFileEntry(
            LocalLibraryItem item,
            LocalBrowseEntryKind kind,
            Dictionary<string, int> copies,
            ISet<string> offlineLocations)
        {
            string title = item.IsMatched
                ? item.MatchedTitle
                : (!string.IsNullOrWhiteSpace(item.ParsedTitle) ? item.ParsedTitle : System.IO.Path.GetFileNameWithoutExtension(item.FileName));

            string detail;

            if (item.IsEpisode)
            {
                detail = EpisodeCode(item);
            }
            else
            {
                detail = item.IsMatched ? item.MatchedYear : (item.ParsedYear.HasValue ? item.ParsedYear.Value.ToString(CultureInfo.InvariantCulture) : null);
            }

            string badge = string.Empty;
            int extra;

            if (offlineLocations != null && offlineLocations.Contains(item.LocationPath ?? string.Empty))
            {
                badge = "Offline";
            }
            else if (item.IsAmbiguous)
            {
                badge = "Check name";
            }
            else if (item.MatchStatus == LocalMatchStatus.NeedsReview)
            {
                badge = "Check match";
            }
            else if (!item.IsMatched)
            {
                badge = "Unmatched";
            }
            else if (copies.TryGetValue(item.Id, out extra))
            {
                badge = (extra + 1) + " copies";
            }

            return new LocalBrowseEntry
            {
                Kind = kind,
                Key = item.Id,
                ShowKey = item.IsEpisode ? ShowKeyFor(item) : null,
                Title = title,
                Detail = detail ?? string.Empty,
                Badge = badge,
                PosterUrl = item.PosterUrl,
                Item = item,
                IsMatched = item.IsMatched
            };
        }

        public static string EpisodeCode(LocalLibraryItem item)
        {
            string code = (item.ParsedSeason.HasValue
                              ? "S" + item.ParsedSeason.Value.ToString("00", CultureInfo.InvariantCulture)
                              : string.Empty) +
                          "E" + (item.ParsedEpisode ?? 0).ToString("00", CultureInfo.InvariantCulture);

            if (item.ParsedEpisodeEnd.HasValue)
            {
                code += "-E" + item.ParsedEpisodeEnd.Value.ToString("00", CultureInfo.InvariantCulture);
            }

            return code;
        }

        private static int EffectiveSeason(LocalLibraryItem item)
        {
            return item.ParsedSeason ?? 1;
        }

        private static List<LocalBrowseEntry> Search(List<LocalBrowseEntry> entries, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return entries;
            }

            string[] tokens = LocalMediaMatchingService.NormaliseTitle(searchText)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                return entries;
            }

            return entries.Where(entry =>
            {
                string haystack = LocalMediaMatchingService.NormaliseTitle(
                    entry.Title + " " + entry.Detail + " " +
                    (entry.Item != null ? entry.Item.FileName : string.Empty));

                return tokens.All(token => haystack.Contains(token));
            }).ToList();
        }

        private static List<LocalBrowseEntry> Sort(List<LocalBrowseEntry> entries)
        {
            return entries
                .OrderBy(e => LocalMediaMatchingService.NormaliseTitle(e.Title), StringComparer.Ordinal)
                .ThenBy(e => e.Detail, StringComparer.Ordinal)
                .ToList();
        }
    }
}
