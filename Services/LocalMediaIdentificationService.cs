using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA IDENTIFICATION SERVICE
    // =============================================================
    //
    // Filename-only classification: "does this look like a TV episode, an
    // anime episode or a movie, and what title / year / season / episode
    // can be read out of the file name (and its folders)." Deliberately
    // separate from scanning (LocalMediaScanService), persistence
    // (LocalMediaIndexStore) and metadata matching
    // (LocalMediaMatchingService) - this class has no filesystem access
    // beyond string handling and no network dependency.
    //
    // Recognised (in this order, first confident reading wins):
    //   1. TV  "Show.Name.S01E02", "Show Name - S01E02", "s01e02",
    //          "S01E01E02" / "S01E01-E03" / "S01E01-03" (multi-episode),
    //          "1x02", "Season 1 Episode 2"
    //   2. Anime fansub  "[Group] Title - 05 [1080p]"
    //   3. Season folder + bare episode  "Show/Season 2/05 - Name.mkv",
    //          "Show/Season 2/E05.mkv"
    //   4. "Title - 05" next to a folder carrying the same title
    //   5. Movie  "Title (2019)", "Title.2019.1080p", with the year taken
    //          as the LAST plausible year token that has a title before it,
    //          so a number inside the title ("Blade Runner 2049 2017",
    //          "1917 2019", "2012 2009") is never mistaken for the year.
    // Anything else classifies as Unknown (title preserved). A reading the
    // parser cannot decide between is flagged IsAmbiguous rather than
    // guessed. Whether something is really anime is decided later by
    // metadata matching; here it is only a hint (LooksLikeAnime).
    public static class LocalMediaIdentificationService
    {
        // =========================================================
        // SUPPORTED VIDEO EXTENSIONS
        // =========================================================
        public static readonly string[] SupportedVideoExtensions =
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts",
            ".mpg", ".mpeg", ".flv", ".ogm", ".divx"
        };

        public static bool IsSupportedVideoFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string extension = Path.GetExtension(fileName);

            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            foreach (string supported in SupportedVideoExtensions)
            {
                if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // =========================================================
        // PATTERNS
        // =========================================================

        // "S01E02", "S01E01E02", "S01E01-E03", "S01E01-03", "S01.E02".
        // The trailing group only accepts further episode numbers shaped
        // like E## or -## / -E## that are not followed by a digit or a
        // resolution/codec letter, so "S01E01 - 1080p" is a single episode.
        private static readonly Regex SeasonEpisodePattern = new Regex(
            @"(?<![A-Za-z0-9])[Ss](?<season>\d{1,2})[\s._-]?[Ee](?<ep>\d{1,3})" +
            @"(?<more>(?:[-_ .]?[Ee]\d{1,3}|-[Ee]?\d{1,3}(?![\dpPiIkKxX]))*)",
            RegexOptions.Compiled);

        // "1x02", "1x01-02" (never "1920x1080": the left edge must not be
        // part of a longer number or word).
        private static readonly Regex CrossEpisodePattern = new Regex(
            @"(?<![A-Za-z0-9])(?<season>\d{1,2})[xX](?<ep>\d{1,3})(?<more>-\d{1,3}(?![\dpPiIkKxX]))?(?![\dpPiIkKxX])",
            RegexOptions.Compiled);

        private static readonly Regex VerboseEpisodePattern = new Regex(
            @"(?<![A-Za-z0-9])[Ss]eason[\s._-]*(?<season>\d{1,2})[\s._-]*[Ee]p(?:isode)?[\s._-]*(?<ep>\d{1,3})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "[Group] Title - 05 ...", "[Group] Title - 05v2 [1080p]".
        private static readonly Regex AnimeFansubPattern = new Regex(
            @"^\s*\[(?<group>[^\]]+)\]\s*(?<title>.+?)\s+[-\u2013]\s+(?<ep>\d{1,4})(?:v\d)?(?![\dA-Za-z])",
            RegexOptions.Compiled);

        // "Title - 05" with no brackets. Only trusted together with folder
        // context (see ClassifyBareEpisode).
        private static readonly Regex DashEpisodePattern = new Regex(
            @"^(?<title>.+?)\s+[-\u2013]\s+(?<ep>\d{1,3})(?:v\d)?(?![\dA-Za-z])",
            RegexOptions.Compiled);

        // "05", "05 - Name", "E05", "Ep 5", "Episode 05 Name".
        private static readonly Regex BareEpisodePattern = new Regex(
            @"^(?:[Ee](?:p(?:isode)?)?[\s._-]*)?(?<ep>\d{1,3})(?![\dA-Za-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SeasonFolderPattern = new Regex(
            @"^(?:season|series|saison|staffel|s)[\s._-]*(?<season>\d{1,2})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex YearTokenPattern = new Regex(
            @"(?<![\dA-Za-z])(?<year>(?:19|20)\d{2})(?![\dA-Za-z])",
            RegexOptions.Compiled);

        private static readonly Regex BracketTagPattern = new Regex(
            @"\[[^\]]*\]",
            RegexOptions.Compiled);

        private static readonly Regex ParenYearPattern = new Regex(
            @"[\(\[]\s*(?<year>(?:19|20)\d{2})\s*[\)\]]",
            RegexOptions.Compiled);

        // First release-style token (resolution, source, codec ...). Used
        // to cut a title that has no year to stop at.
        private static readonly Regex ReleaseTokenPattern = new Regex(
            @"(?<![A-Za-z0-9])(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|bdremux|remux|webrip|web-dl|webdl|hdtv|dvdrip|hdrip|hdr10|x264|x265|h264|h265|h\.264|h\.265|hevc|xvid|divx|10bit|repack)(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Plex-style extras suffix: "Movie (2010)-trailer", "Title-featurette".
        private static readonly Regex ExtrasSuffixPattern = new Regex(
            @"-(?:trailer|behindthescenes|deleted|featurette|interview|scene)\d*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SampleTokenPattern = new Regex(
            @"(?:^|[\s._\-\[\(])sample(?:$|[\s._\-\]\)])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TrailerTrailingPattern = new Regex(
            @"(?:^|[\s._\-\[\(])trailer\d*[\]\)]?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> ExtrasFolderNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "extras", "extra", "featurettes", "featurette", "behind the scenes",
            "deleted scenes", "interviews", "trailers", "trailer", "bonus",
            "bonus features", "samples", "sample"
        };

        private static readonly HashSet<string> SampleFolderNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { "sample", "samples" };

        // Folder names that carry no title information.
        private static readonly HashSet<string> GenericFolderNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "movies", "movie", "films", "film", "tv", "tv shows", "tv series", "shows", "series",
            "anime", "video", "videos", "downloads", "media", "specials", "special", "complete"
        };

        private const long SampleSizeLimitBytes = 200L * 1024 * 1024;
        private const long TrailerSizeLimitBytes = 500L * 1024 * 1024;

        // =========================================================
        // EXTRAS / SAMPLES
        // =========================================================
        //
        // Returns a short fixed reason ("sample", "trailer", "extra") when
        // the file is clearly not main media, otherwise null. Conservative:
        // "Trailer Park Boys S01E01" (token not trailing) and any large
        // file named like a sample are kept.
        public static string GetExclusionReason(string relativePath, long sizeBytes)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            string stem = Path.GetFileNameWithoutExtension(relativePath) ?? string.Empty;
            string[] folders = GetFolderSegments(relativePath);

            bool inSampleFolder = false;

            foreach (string folder in folders)
            {
                if (SampleFolderNames.Contains(folder.Trim()))
                {
                    inSampleFolder = true;
                }
                else if (ExtrasFolderNames.Contains(folder.Trim()))
                {
                    return "extra";
                }
            }

            if ((inSampleFolder || SampleTokenPattern.IsMatch(stem) ||
                 string.Equals(stem, "sample", StringComparison.OrdinalIgnoreCase)) &&
                sizeBytes < SampleSizeLimitBytes)
            {
                return "sample";
            }

            bool hasEpisodeMarker = SeasonEpisodePattern.IsMatch(stem) || CrossEpisodePattern.IsMatch(stem);

            if (!hasEpisodeMarker &&
                (TrailerTrailingPattern.IsMatch(stem) ||
                 string.Equals(stem, "trailer", StringComparison.OrdinalIgnoreCase)) &&
                sizeBytes < TrailerSizeLimitBytes)
            {
                return "trailer";
            }

            if (!hasEpisodeMarker && ExtrasSuffixPattern.IsMatch(stem))
            {
                return "extra";
            }

            return null;
        }

        // =========================================================
        // CLASSIFY
        // =========================================================

        public static LocalMediaFile Classify(string fullPath)
        {
            return Classify(fullPath, null);
        }

        // relativePath is the file's path below its Library Location root.
        // Only its folders are consulted for context (season folders, show
        // folder, movie folder) - never anything above the root.
        public static LocalMediaFile Classify(string fullPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            string stem = Path.GetFileNameWithoutExtension(fullPath) ?? string.Empty;

            var result = new LocalMediaFile
            {
                FullPath = fullPath,
                RelativePath = relativePath,
                FileName = Path.GetFileName(fullPath),
                Kind = LocalMediaKind.Unknown
            };

            string[] folders = string.IsNullOrWhiteSpace(relativePath)
                ? new string[0]
                : GetFolderSegments(relativePath);

            int? folderSeason = null;
            string contextTitle = null;
            int? contextYear = null;
            FindFolderContext(folders, out folderSeason, out contextTitle, out contextYear);

            // ---- 1. Standard TV markers --------------------------------
            if (TryClassifyStandardTv(stem, result, contextTitle, folderSeason))
            {
                return result;
            }

            // ---- 2. Anime fansub ---------------------------------------
            Match anime = AnimeFansubPattern.Match(stem);

            if (anime.Success)
            {
                int episode;

                if (int.TryParse(anime.Groups["ep"].Value, out episode) && !IsYearLike(episode))
                {
                    result.Kind = LocalMediaKind.TvEpisode;
                    result.LooksLikeAnime = true;
                    result.ParsedEpisode = episode;
                    result.ParsedSeason = folderSeason;
                    ApplyTitle(result, anime.Groups["title"].Value);
                    return result;
                }
            }

            // ---- 3 / 4. Bare episode numbers with folder context -------
            if (TryClassifyBareEpisode(stem, result, folderSeason, contextTitle))
            {
                return result;
            }

            // ---- 5. Movie ----------------------------------------------
            ClassifyMovieOrUnknown(stem, result, contextTitle, contextYear);

            return result;
        }

        private static bool TryClassifyStandardTv(
            string stem,
            LocalMediaFile result,
            string contextTitle,
            int? folderSeason)
        {
            MatchCollection seMatches = SeasonEpisodePattern.Matches(stem);
            Match chosen = null;
            bool ambiguous = false;

            if (seMatches.Count > 0)
            {
                chosen = seMatches[0];

                // Two different SxxEyy markers in one name cannot be
                // resolved by guessing.
                for (int i = 1; i < seMatches.Count; i++)
                {
                    if (seMatches[i].Groups["season"].Value != chosen.Groups["season"].Value ||
                        seMatches[i].Groups["ep"].Value != chosen.Groups["ep"].Value)
                    {
                        ambiguous = true;
                    }
                }
            }
            else
            {
                chosen = CrossEpisodePattern.Match(stem);

                if (!chosen.Success)
                {
                    chosen = VerboseEpisodePattern.Match(stem);
                }

                if (!chosen.Success)
                {
                    return false;
                }
            }

            int season;
            int episode;

            if (!int.TryParse(chosen.Groups["season"].Value, out season) ||
                !int.TryParse(chosen.Groups["ep"].Value, out episode))
            {
                return false;
            }

            result.Kind = LocalMediaKind.TvEpisode;
            result.ParsedSeason = season;
            result.ParsedEpisode = episode;

            int? end = ParseEpisodeEnd(chosen.Groups["more"].Value, episode);

            if (end.HasValue)
            {
                if (end.Value > episode && end.Value - episode < 50)
                {
                    result.ParsedEpisodeEnd = end.Value;
                }
                else if (end.Value != episode)
                {
                    ambiguous = true;
                    SetAmbiguity(result, "The episode range in the file name could not be read.");
                }
            }

            string rawTitle = stem.Substring(0, chosen.Index);

            // "[Group] Show - S01E02": the group tag is a fansub hint and
            // must not stay in the title.
            if (Regex.IsMatch(rawTitle, @"^\s*\[[^\]]+\]"))
            {
                result.LooksLikeAnime = true;
            }

            ApplyTitle(result, rawTitle);

            if (string.IsNullOrWhiteSpace(result.ParsedTitle))
            {
                result.ParsedTitle = contextTitle;
            }

            if (ambiguous && !result.IsAmbiguous)
            {
                SetAmbiguity(result, "The file name contains more than one season/episode marker.");
            }

            if (folderSeason.HasValue && folderSeason.Value != season && !result.IsAmbiguous)
            {
                SetAmbiguity(result, "The season folder and the file name disagree.");
            }

            return true;
        }

        private static bool TryClassifyBareEpisode(
            string stem,
            LocalMediaFile result,
            int? folderSeason,
            string contextTitle)
        {
            Match dash = DashEpisodePattern.Match(stem);

            if (dash.Success && !YearTokenPattern.IsMatch(dash.Groups["title"].Value + " "))
            {
                int dashEpisode;
                string dashTitle = CleanTitle(StripBracketTags(dash.Groups["title"].Value));

                if (int.TryParse(dash.Groups["ep"].Value, out dashEpisode) &&
                    !string.IsNullOrWhiteSpace(dashTitle))
                {
                    // Trusted only with context: a season folder, or a folder
                    // carrying the same title. Otherwise "Title - 2" could be
                    // a movie or a sequel - reported, not guessed.
                    bool titleMatchesFolder = !string.IsNullOrWhiteSpace(contextTitle) &&
                        string.Equals(NormaliseLoose(dashTitle), NormaliseLoose(contextTitle), StringComparison.Ordinal);

                    if (folderSeason.HasValue || titleMatchesFolder)
                    {
                        result.Kind = LocalMediaKind.TvEpisode;
                        result.ParsedTitle = dashTitle;
                        result.ParsedSeason = folderSeason;
                        result.ParsedEpisode = dashEpisode;
                        result.LooksLikeAnime = !folderSeason.HasValue;
                        return true;
                    }

                    result.Kind = LocalMediaKind.Unknown;
                    result.ParsedTitle = CleanTitle(StripBracketTags(stem));
                    SetAmbiguity(result, "This name looks like an episode number but there is no season or show folder to confirm it.");
                    return true;
                }
            }

            if (folderSeason.HasValue)
            {
                Match bare = BareEpisodePattern.Match(stem);
                int bareEpisode;

                if (bare.Success && int.TryParse(bare.Groups["ep"].Value, out bareEpisode))
                {
                    result.Kind = LocalMediaKind.TvEpisode;
                    result.ParsedTitle = contextTitle;
                    result.ParsedSeason = folderSeason;
                    result.ParsedEpisode = bareEpisode;

                    if (string.IsNullOrWhiteSpace(contextTitle))
                    {
                        SetAmbiguity(result, "There is a season folder but no show folder to name the series.");
                    }

                    return true;
                }
            }

            return false;
        }

        private static void ClassifyMovieOrUnknown(
            string stem,
            LocalMediaFile result,
            string contextTitle,
            int? contextYear)
        {
            string working = StripBracketTagsKeepYear(stem);

            int? year;
            string title;
            bool multipleParenYears;
            SplitTitleAndYear(working, out title, out year, out multipleParenYears);

            if (year.HasValue && !string.IsNullOrWhiteSpace(title))
            {
                result.Kind = LocalMediaKind.Movie;
                result.ParsedTitle = CleanTitle(title);
                result.ParsedYear = year;

                if (multipleParenYears)
                {
                    SetAmbiguity(result, "The file name contains more than one year.");
                }

                return;
            }

            // No year in the file name: the title stops at the first
            // release token, and the folder may supply the year.
            string noYearTitle = CleanTitle(CutAtReleaseToken(working));

            if (string.IsNullOrWhiteSpace(noYearTitle))
            {
                noYearTitle = CleanTitle(StripBracketTags(stem));
            }

            if (!string.IsNullOrWhiteSpace(contextTitle) && contextYear.HasValue &&
                (string.IsNullOrWhiteSpace(noYearTitle) ||
                 string.Equals(NormaliseLoose(noYearTitle), NormaliseLoose(contextTitle), StringComparison.Ordinal)))
            {
                result.Kind = LocalMediaKind.Movie;
                result.ParsedTitle = contextTitle;
                result.ParsedYear = contextYear;
                return;
            }

            result.Kind = LocalMediaKind.Unknown;
            result.ParsedTitle = noYearTitle;
        }

        // =========================================================
        // TITLE / YEAR HELPERS
        // =========================================================

        // Splits "Title.2019.1080p" / "Title (2019)" into title + year.
        // The year is the last plausible year token that has a non-empty
        // title before it; a parenthesised year always wins.
        private static void SplitTitleAndYear(
            string text,
            out string title,
            out int? year,
            out bool multipleParenYears)
        {
            title = null;
            year = null;
            multipleParenYears = false;

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            MatchCollection parens = ParenYearPattern.Matches(text);

            if (parens.Count > 0)
            {
                Match last = parens[parens.Count - 1];
                int parsed;

                if (int.TryParse(last.Groups["year"].Value, out parsed) && IsPlausibleYear(parsed))
                {
                    string before = text.Substring(0, last.Index);

                    if (!string.IsNullOrWhiteSpace(CleanTitle(before)))
                    {
                        year = parsed;
                        title = before;

                        for (int i = 0; i < parens.Count - 1; i++)
                        {
                            int other;

                            if (int.TryParse(parens[i].Groups["year"].Value, out other) && other != parsed)
                            {
                                multipleParenYears = true;
                            }
                        }

                        return;
                    }
                }
            }

            MatchCollection tokens = YearTokenPattern.Matches(text);

            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                int parsed;

                if (!int.TryParse(tokens[i].Groups["year"].Value, out parsed) || !IsPlausibleYear(parsed))
                {
                    continue;
                }

                string before = text.Substring(0, tokens[i].Index);

                if (string.IsNullOrWhiteSpace(CleanTitle(before)))
                {
                    continue;
                }

                year = parsed;
                title = before;
                return;
            }
        }

        private static bool IsPlausibleYear(int year)
        {
            return year >= 1900 && year <= DateTime.UtcNow.Year + 1;
        }

        private static bool IsYearLike(int number)
        {
            return number >= 1900 && number <= 2099;
        }

        private static string CutAtReleaseToken(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            Match token = ReleaseTokenPattern.Match(text);

            return token.Success && token.Index > 0 ? text.Substring(0, token.Index) : text;
        }

        private static string StripBracketTags(string text)
        {
            return string.IsNullOrEmpty(text) ? text : BracketTagPattern.Replace(text, " ");
        }

        // Removes [tag] groups but keeps "[2019]" so a bracketed year still
        // reads as a year.
        private static string StripBracketTagsKeepYear(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return BracketTagPattern.Replace(text, m =>
                ParenYearPattern.IsMatch(m.Value) ? "(" + m.Value.Trim('[', ']') + ")" : " ");
        }

        private static void ApplyTitle(LocalMediaFile result, string rawTitle)
        {
            string working = StripBracketTagsKeepYear(rawTitle ?? string.Empty);
            string title;
            int? year;
            bool multiple;
            SplitTitleAndYear(working + " ", out title, out year, out multiple);

            // A trailing year after a show title ("Show (2019) S01E02") is
            // only a disambiguator for matching, not part of the name.
            if (year.HasValue && !string.IsNullOrWhiteSpace(title))
            {
                result.ParsedYear = year;
                result.ParsedTitle = CleanTitle(title);
                return;
            }

            result.ParsedTitle = CleanTitle(CutAtReleaseToken(working));
        }

        private static string CleanTitle(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return rawTitle == null ? null : string.Empty;
            }

            string cleaned = rawTitle.Replace('.', ' ').Replace('_', ' ');
            cleaned = cleaned.Replace("()", " ").Replace("( )", " ").Trim(' ', '-', '(', ')', '[', ']');

            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }

            return cleaned.Trim(' ', '-');
        }

        // Lower-case alphanumerics only - a loose equality key for "is this
        // the same title" decisions inside the parser.
        internal static string NormaliseLoose(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        private static int? ParseEpisodeEnd(string more, int firstEpisode)
        {
            if (string.IsNullOrEmpty(more))
            {
                return null;
            }

            int? last = null;

            foreach (Match number in Regex.Matches(more, @"\d{1,3}"))
            {
                int value;

                if (int.TryParse(number.Value, out value))
                {
                    last = value;
                }
            }

            return last;
        }

        private static void SetAmbiguity(LocalMediaFile result, string reason)
        {
            result.IsAmbiguous = true;
            result.AmbiguityReason = reason;
        }

        // =========================================================
        // FOLDER CONTEXT
        // =========================================================

        private static string[] GetFolderSegments(string relativePath)
        {
            string directory = Path.GetDirectoryName(relativePath);

            if (string.IsNullOrEmpty(directory))
            {
                return new string[0];
            }

            return directory.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
        }

        // Walks the folders from the deepest upward: the first season
        // folder gives the season; the first non-season, non-generic folder
        // after it gives the show / movie title (and a year if it has one).
        private static void FindFolderContext(
            string[] folders,
            out int? season,
            out string title,
            out int? year)
        {
            season = null;
            title = null;
            year = null;

            for (int i = folders.Length - 1; i >= 0; i--)
            {
                string folder = folders[i].Trim();
                Match seasonMatch = SeasonFolderPattern.Match(folder);

                if (seasonMatch.Success)
                {
                    int parsed;

                    if (!season.HasValue && int.TryParse(seasonMatch.Groups["season"].Value, out parsed))
                    {
                        season = parsed;
                    }

                    continue;
                }

                if (string.Equals(folder, "specials", StringComparison.OrdinalIgnoreCase))
                {
                    if (!season.HasValue)
                    {
                        season = 0;
                    }

                    continue;
                }

                if (GenericFolderNames.Contains(folder) || ExtrasFolderNames.Contains(folder))
                {
                    continue;
                }

                string folderTitle;
                int? folderYear;
                bool multiple;
                string working = StripBracketTagsKeepYear(folder);
                SplitTitleAndYear(working, out folderTitle, out folderYear, out multiple);

                if (folderYear.HasValue && !string.IsNullOrWhiteSpace(folderTitle))
                {
                    title = CleanTitle(folderTitle);
                    year = folderYear;
                }
                else
                {
                    title = CleanTitle(CutAtReleaseToken(working));
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return;
                }
            }
        }

        // =========================================================
        // SELF-CHECKS
        // =========================================================
        //
        // Dependency-free diagnostic entry point, same convention as
        // PlaybackSourceNormalizationService.RunSelfChecks: returns one
        // "PASS - / FAIL - / ERROR - " line per check; not wired to any UI
        // and never run automatically.
        public static List<string> RunSelfChecks()
        {
            var results = new List<string>();

            results.Add(RunCheck("Movie 'Title (2019)' reads title and year", () =>
            {
                LocalMediaFile f = Classify(@"C:\m\Some Movie (2019).mkv");
                return f.Kind == LocalMediaKind.Movie && f.ParsedTitle == "Some Movie" && f.ParsedYear == 2019;
            }));

            results.Add(RunCheck("Movie 'Blade.Runner.2049.2017.1080p' keeps 2049 in the title", () =>
            {
                LocalMediaFile f = Classify(@"C:\m\Blade.Runner.2049.2017.1080p.BluRay.x264.mkv");
                return f.Kind == LocalMediaKind.Movie && f.ParsedTitle == "Blade Runner 2049" && f.ParsedYear == 2017;
            }));

            results.Add(RunCheck("Movie '1917.2019.1080p' keeps 1917 as the title", () =>
            {
                LocalMediaFile f = Classify(@"C:\m\1917.2019.1080p.WEB-DL.mkv");
                return f.Kind == LocalMediaKind.Movie && f.ParsedTitle == "1917" && f.ParsedYear == 2019;
            }));

            results.Add(RunCheck("Movie '2012 (2009)' keeps 2012 as the title", () =>
            {
                LocalMediaFile f = Classify(@"C:\m\2012 (2009).mp4");
                return f.Kind == LocalMediaKind.Movie && f.ParsedTitle == "2012" && f.ParsedYear == 2009;
            }));

            results.Add(RunCheck("A future-year number is not taken as a year", () =>
            {
                LocalMediaFile f = Classify(@"C:\m\Mission.2077.mkv");
                return f.Kind == LocalMediaKind.Unknown && f.ParsedYear == null;
            }));

            results.Add(RunCheck("TV 'Show.Name.S01E02' reads show, season, episode", () =>
            {
                LocalMediaFile f = Classify(@"C:\t\Show.Name.S01E02.720p.mkv");
                return f.Kind == LocalMediaKind.TvEpisode && f.ParsedTitle == "Show Name" &&
                       f.ParsedSeason == 1 && f.ParsedEpisode == 2 && f.ParsedEpisodeEnd == null;
            }));

            results.Add(RunCheck("TV 'Show - S02E10' lower-case 's02e10' also reads", () =>
            {
                LocalMediaFile f = Classify(@"C:\t\Show - s02e10.mkv");
                return f.Kind == LocalMediaKind.TvEpisode && f.ParsedSeason == 2 && f.ParsedEpisode == 10;
            }));

            results.Add(RunCheck("TV 'S01E01E02' is a two-episode file", () =>
            {
                LocalMediaFile f = Classify(@"C:\t\Show.S01E01E02.mkv");
                return f.ParsedEpisode == 1 && f.ParsedEpisodeEnd == 2;
            }));

            results.Add(RunCheck("TV 'S01E03-E05' is a three-episode file", () =>
            {
                LocalMediaFile f = Classify(@"C:\t\Show.S01E03-E05.mkv");
                return f.ParsedEpisode == 3 && f.ParsedEpisodeEnd == 5;
            }));

            results.Add(RunCheck("TV 'S01E01 - 1080p' is NOT a multi-episode file", () =>
            {
                LocalMediaFile f = Classify(@"C:\t\Show S01E01 - 1080p.mkv");
                return f.ParsedEpisode == 1 && f.ParsedEpisodeEnd == null;
            }));

            results.Add(RunCheck("TV '1x02' reads season 1 episode 2, '1920x1080' does not", () =>
            {
                LocalMediaFile a = Classify(@"C:\t\Show 1x02.avi");
                LocalMediaFile b = Classify(@"C:\t\Clip 1920x1080.avi");
                return a.Kind == LocalMediaKind.TvEpisode && a.ParsedSeason == 1 && a.ParsedEpisode == 2 &&
                       b.Kind != LocalMediaKind.TvEpisode;
            }));

            results.Add(RunCheck("Anime '[Group] Title - 05' reads episode 5, no season", () =>
            {
                LocalMediaFile f = Classify(@"C:\a\[SubsPlease] Some Anime - 05 (1080p) [ABCD1234].mkv");
                return f.Kind == LocalMediaKind.TvEpisode && f.LooksLikeAnime && f.ParsedTitle == "Some Anime" &&
                       f.ParsedEpisode == 5 && f.ParsedSeason == null;
            }));

            results.Add(RunCheck("Season folder + bare episode names the show from its folder", () =>
            {
                LocalMediaFile f = Classify(@"D:\TV\Cool Show\Season 2\05 - Pilot.mkv", @"Cool Show\Season 2\05 - Pilot.mkv");
                return f.Kind == LocalMediaKind.TvEpisode && f.ParsedTitle == "Cool Show" &&
                       f.ParsedSeason == 2 && f.ParsedEpisode == 5;
            }));

            results.Add(RunCheck("'Title - 5' with no folder context is ambiguous, not guessed", () =>
            {
                LocalMediaFile f = Classify(@"D:\X\Some Title - 5.mkv", @"Some Title - 5.mkv");
                return f.Kind == LocalMediaKind.Unknown && f.IsAmbiguous;
            }));

            results.Add(RunCheck("Season folder that disagrees with the file name is flagged", () =>
            {
                LocalMediaFile f = Classify(@"D:\TV\Show\Season 3\Show.S02E05.mkv", @"Show\Season 3\Show.S02E05.mkv");
                return f.ParsedSeason == 2 && f.IsAmbiguous;
            }));

            results.Add(RunCheck("Movie folder supplies the year when the file name has none", () =>
            {
                LocalMediaFile f = Classify(@"D:\M\Inception (2010)\Inception.mkv", @"Inception (2010)\Inception.mkv");
                return f.Kind == LocalMediaKind.Movie && f.ParsedTitle == "Inception" && f.ParsedYear == 2010;
            }));

            results.Add(RunCheck("Sample and trailer files are excluded, real media is not", () =>
            {
                return GetExclusionReason(@"Movie\Movie.2019.sample.mkv", 20L * 1024 * 1024) == "sample" &&
                       GetExclusionReason(@"Movie\Movie (2019)-trailer.mkv", 40L * 1024 * 1024) == "trailer" &&
                       GetExclusionReason(@"Movie\Extras\Making of.mkv", 900L * 1024 * 1024) == "extra" &&
                       GetExclusionReason(@"Trailer Park Boys\Trailer.Park.Boys.S01E01.mkv", 300L * 1024 * 1024) == null &&
                       GetExclusionReason(@"Movie\Movie.2019.sample.mkv", 4000L * 1024 * 1024) == null;
            }));

            results.Add(RunCheck("Added extensions are recognised", () =>
            {
                return IsSupportedVideoFile("a.mpg") && IsSupportedVideoFile("a.MPEG") &&
                       IsSupportedVideoFile("a.flv") && IsSupportedVideoFile("a.ogm") &&
                       IsSupportedVideoFile("a.divx") && !IsSupportedVideoFile("a.srt");
            }));

            return results;
        }

        private static string RunCheck(string description, Func<bool> check)
        {
            try
            {
                return (check() ? "PASS - " : "FAIL - ") + description;
            }
            catch (Exception ex)
            {
                return "ERROR - " + description + " (" + ex.GetType().Name + ")";
            }
        }
    }
}
