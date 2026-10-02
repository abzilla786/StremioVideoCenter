using StremioVideoCenter.Models;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // LOCAL MEDIA IDENTIFICATION SERVICE (Slice 15 - architecture only)
    // =============================================================
    //
    // Filename-only classification: "does this look like a TV episode or
    // a movie, and what title/year/season/episode can be read straight
    // out of the filename." Deliberately separate from scanning
    // (LocalMediaScanService) and from library persistence
    // (LocalLibraryService) - a distinct concern, per this slice's own
    // instruction to "separate filesystem discovery / media
    // identification / metadata enrichment / library persistence /
    // playback into distinct concerns."
    //
    // WHAT THIS DOES NOT DO (explicitly deferred)
    // --------------------------------------------
    // This never calls Cinemeta/OMDb - it has no network dependency
    // at all. Turning a parsed "Title"/"Year" into a real Cinemeta/IMDb
    // match (the "Metadata matching" stage of the pipeline this slice's
    // spec describes) is a distinct, future concern: it needs fuzzy
    // title matching against a real catalogue/search provider, ambiguity
    // handling (multiple candidate matches), and a decision about which
    // provider to try first - exactly the kind of design decision this
    // architecture-only slice should not make silently. LocalMediaFile.
    // MatchedStremioId stays null until that slice exists.
    //
    // Anime is deliberately not distinguished from TvEpisode here - see
    // LocalMediaKind's remarks.
    public static class LocalMediaIdentificationService
    {
        // =========================================================
        // SUPPORTED VIDEO EXTENSIONS
        // =========================================================
        //
        // Common container formats MPV already plays (this project's
        // playback pipeline is MPV-based throughout - see
        // Controls/Player) - not an exhaustive codec/container survey,
        // a practical common-case list.
        public static readonly string[] SupportedVideoExtensions =
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts"
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
        // FILENAME PATTERNS
        // =========================================================
        //
        // Two conservative, widely-used release-naming conventions -
        // matched, not guessed. A filename that matches neither pattern
        // classifies as Unknown rather than a best-effort wrong guess,
        // consistent with this codebase's existing "never guessed, never
        // inferred" convention for text-token parsing (see
        // PlaybackSourceNormalizationService's own remarks).

        // "Show.Name.S01E02...", "Show Name - S01E02", "Show Name 1x02"
        private static readonly Regex TvEpisodePattern = new Regex(
            @"^(?<title>.+?)[\.\s_\-]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,3})",
            RegexOptions.Compiled);

        private static readonly Regex TvEpisodeAltPattern = new Regex(
            @"^(?<title>.+?)[\.\s_\-]+(?<season>\d{1,2})[xX](?<episode>\d{1,3})",
            RegexOptions.Compiled);

        // "Movie.Title.2019...", "Movie Title (2019)"
        private static readonly Regex MovieYearPattern = new Regex(
            @"^(?<title>.+?)[\.\s_\-\(]+(?<year>19\d{2}|20\d{2})\)?",
            RegexOptions.Compiled);

        // =========================================================
        // CLASSIFY
        // =========================================================

        public static LocalMediaFile Classify(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            string fileName = Path.GetFileNameWithoutExtension(fullPath);

            var result = new LocalMediaFile
            {
                FullPath = fullPath,
                FileName = Path.GetFileName(fullPath),
                Kind = LocalMediaKind.Unknown
            };

            Match tvMatch = TvEpisodePattern.Match(fileName);

            if (!tvMatch.Success)
            {
                tvMatch = TvEpisodeAltPattern.Match(fileName);
            }

            if (tvMatch.Success)
            {
                result.Kind = LocalMediaKind.TvEpisode;
                result.ParsedTitle = CleanTitle(tvMatch.Groups["title"].Value);
                result.ParsedSeason = ParseIntOrNull(tvMatch.Groups["season"].Value);
                result.ParsedEpisode = ParseIntOrNull(tvMatch.Groups["episode"].Value);
                return result;
            }

            Match movieMatch = MovieYearPattern.Match(fileName);

            if (movieMatch.Success)
            {
                result.Kind = LocalMediaKind.Movie;
                result.ParsedTitle = CleanTitle(movieMatch.Groups["title"].Value);
                result.ParsedYear = ParseIntOrNull(movieMatch.Groups["year"].Value);
                return result;
            }

            // Unknown - the raw filename is preserved as the title so the
            // file is still identifiable in a listing, but Kind stays
            // Unknown rather than guessing Movie.
            result.ParsedTitle = CleanTitle(fileName);

            return result;
        }

        private static string CleanTitle(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return rawTitle;
            }

            string cleaned = rawTitle.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-');

            while (cleaned.Contains("  "))
            {
                cleaned = cleaned.Replace("  ", " ");
            }

            return cleaned;
        }

        private static int? ParseIntOrNull(string value)
        {
            int parsed;

            if (int.TryParse(value, out parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
