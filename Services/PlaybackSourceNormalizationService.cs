using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Services
{
    // Converts a PlaybackSource (plus its OriginatingStream back-reference)
    // into a NormalizedPlaybackSource - a consistent, addon-independent
    // representation intended for a future source-selection UI.
    //
    // This is a pure data transformation: no network calls, no Real-Debrid,
    // no Stremio addon calls. It only reshapes data StreamResolverService/
    // PlaybackSourceAggregationService already produced, so nothing here
    // can fail for network/addon reasons - only via a null argument.
    //
    // Deliberately does NOT rank, score, or pick a "best" source - that is
    // explicitly out of scope for this slice, and nothing here orders
    // sources by any notion of quality.
    //
    // Every token table below is deliberately conservative and explicit:
    // exact, case-insensitive substring matches only, no regex, no
    // release-name parsing, no inference from one field to another (e.g.
    // HDR is never assumed just because resolution is 2160p; Atmos is
    // never assumed just because an audio codec is Dolby-branded). If a
    // stream's Name/Title/Description doesn't contain one of these exact
    // tokens, the corresponding field stays null - unknown, not guessed.
    //
    // The official Stremio stream protocol (stremio-addon-sdk) has no
    // structured field anywhere for resolution, HDR, video codec,
    // container, audio codec, or Atmos - "name" is documented as "usually
    // used for stream quality", i.e. free text by convention. Every token
    // table here exists because text parsing is the only route that could
    // ever populate these fields for any real addon, not as a shortcut
    // around a structured alternative that doesn't exist.
    public class PlaybackSourceNormalizationService
    {
        // =========================================================
        // TOKEN TABLES
        // =========================================================

        private static readonly KeyValuePair<string, int>[] KnownResolutionTokens = new[]
        {
            new KeyValuePair<string, int>("2160p", 2160),
            new KeyValuePair<string, int>("4k", 2160),
            new KeyValuePair<string, int>("uhd", 2160),
            new KeyValuePair<string, int>("1440p", 1440),
            new KeyValuePair<string, int>("2k", 1440),
            new KeyValuePair<string, int>("1080p", 1080),
            new KeyValuePair<string, int>("fhd", 1080),
            new KeyValuePair<string, int>("720p", 720),
            new KeyValuePair<string, int>("576p", 576),
            new KeyValuePair<string, int>("480p", 480),
            new KeyValuePair<string, int>("360p", 360)
        };

        // Short, ambiguous resolution aliases (Slice 14 correction, added
        // alongside Source Selector quality ordering) that need
        // delimiter-aware matching rather than plain substring matching -
        // the same technique already used below for the "dv" HDR token.
        // Bare "hd"/"sd" would otherwise false-positive inside many
        // unrelated release tags ("HDR10", "SDR", "HDCAM", etc).
        private static readonly KeyValuePair<string, int>[] KnownDelimitedResolutionTokens = new[]
        {
            new KeyValuePair<string, int>("hd", 720),
            new KeyValuePair<string, int>("sd", 480)
        };

        // Order matters: longer/more specific tokens are checked before
        // shorter ones they could otherwise be confused with (not a
        // substring risk here, just deliberate grouping by codec family).
        private static readonly KeyValuePair<string, string>[] KnownVideoCodecTokens = new[]
        {
            new KeyValuePair<string, string>("h.265", "H265"),
            new KeyValuePair<string, string>("x265", "H265"),
            new KeyValuePair<string, string>("hevc", "H265"),
            new KeyValuePair<string, string>("h.264", "H264"),
            new KeyValuePair<string, string>("x264", "H264"),
            new KeyValuePair<string, string>("avc", "H264"),
            new KeyValuePair<string, string>("av1", "AV1"),
            new KeyValuePair<string, string>("vp9", "VP9")
        };

        // Order matters here for correctness, not just style: "dts-hd" and
        // "e-ac3"/"eac3" each contain a shorter token ("dts", "ac3") as a
        // literal substring, so the longer/more specific token must be
        // checked first or it would never be reached.
        private static readonly KeyValuePair<string, string>[] KnownAudioCodecTokens = new[]
        {
            new KeyValuePair<string, string>("truehd", "TrueHD"),
            new KeyValuePair<string, string>("dts-hd", "DTS-HD"),
            new KeyValuePair<string, string>("dts", "DTS"),
            new KeyValuePair<string, string>("e-ac3", "E-AC3"),
            new KeyValuePair<string, string>("eac3", "E-AC3"),
            new KeyValuePair<string, string>("ac3", "AC3"),
            new KeyValuePair<string, string>("aac", "AAC")
        };

        private static readonly KeyValuePair<string, string>[] KnownContainerExtensions = new[]
        {
            new KeyValuePair<string, string>(".mkv", "MKV"),
            new KeyValuePair<string, string>(".mp4", "MP4"),
            new KeyValuePair<string, string>(".avi", "AVI"),
            new KeyValuePair<string, string>(".webm", "WebM"),
            new KeyValuePair<string, string>(".ts", "TS")
        };

        // =========================================================
        // NORMALIZE
        // =========================================================

        public NormalizedPlaybackSource Normalize(PlaybackSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            NormalizedPlaybackSource normalized = new NormalizedPlaybackSource
            {
                Source = source,
                Name = source.Name,
                Title = source.Title,
                Url = source.Url,
                SourceAddonUrl = source.SourceAddonUrl,
                SourceAddonName = source.SourceAddonName,
                Kind = source.Kind
            };

            StremioStream stream = source.OriginatingStream;

            // Description is a distinct field from Title (the official
            // protocol's stated eventual replacement for it) - captured
            // separately, never merged with or used as a fallback for
            // Title, and never overwrites it.
            if (stream != null)
            {
                normalized.Description = stream.Description;
            }

            // All text-token parsing searches Name + Title + Description
            // together, since Description is documented as serving the
            // same role Title currently does and could carry the same
            // kind of addon-embedded release/quality text.
            string searchText = BuildSearchText(source.Name, source.Title, normalized.Description);

            normalized.ResolutionHeight =
                TryMatchIntToken(searchText, KnownResolutionTokens) ??
                TryMatchDelimitedIntToken(searchText, KnownDelimitedResolutionTokens);
            normalized.CodecInfo = TryMatchToken(searchText, KnownVideoCodecTokens);
            normalized.HdrInfo = TryParseHdr(searchText);
            normalized.AtmosInfo = TryParseAtmos(searchText);
            normalized.AudioInfo = TryMatchToken(searchText, KnownAudioCodecTokens);

            // AudioChannels intentionally left null/deferred this slice -
            // see NormalizedPlaybackSource.AudioChannels for why.

            if (stream != null && stream.BehaviorHints != null)
            {
                normalized.FileSizeBytes = stream.BehaviorHints.VideoSize;
                normalized.ReleaseFilename = stream.BehaviorHints.Filename;
                normalized.ReleaseName = DeriveReleaseName(stream.BehaviorHints.Filename);
                normalized.ContainerInfo = TryParseContainer(stream.BehaviorHints.Filename);
            }

            if (stream != null && stream.Subtitles != null && stream.Subtitles.Count > 0)
            {
                List<string> languages = new List<string>();

                foreach (StremioSubtitle subtitle in stream.Subtitles)
                {
                    if (subtitle != null &&
                        !string.IsNullOrWhiteSpace(subtitle.Lang) &&
                        !languages.Contains(subtitle.Lang))
                    {
                        languages.Add(subtitle.Lang);
                    }
                }

                if (languages.Count > 0)
                {
                    normalized.SubtitleLanguages = languages;
                }
            }

            return normalized;
        }

        public List<NormalizedPlaybackSource> NormalizeAll(IEnumerable<PlaybackSource> sources)
        {
            List<NormalizedPlaybackSource> results = new List<NormalizedPlaybackSource>();

            if (sources == null)
            {
                return results;
            }

            foreach (PlaybackSource source in sources)
            {
                if (source != null)
                {
                    results.Add(Normalize(source));
                }
            }

            return results;
        }

        // =========================================================
        // PARSING HELPERS
        // =========================================================

        private static string BuildSearchText(string name, string title, string description)
        {
            return ((name ?? string.Empty) + " " +
                    (title ?? string.Empty) + " " +
                    (description ?? string.Empty)).ToLowerInvariant();
        }

        private static int? TryMatchIntToken(string searchText, KeyValuePair<string, int>[] tokens)
        {
            foreach (KeyValuePair<string, int> token in tokens)
            {
                if (searchText.Contains(token.Key))
                {
                    return token.Value;
                }
            }

            return null;
        }

        // Delimiter-aware counterpart to TryMatchIntToken, for short/
        // ambiguous tokens (see KnownDelimitedResolutionTokens) that would
        // false-positive under plain substring matching. Reuses
        // ContainsDelimitedToken, the same helper already relied on for
        // the "dv" HDR token below.
        private static int? TryMatchDelimitedIntToken(string searchText, KeyValuePair<string, int>[] tokens)
        {
            foreach (KeyValuePair<string, int> token in tokens)
            {
                if (ContainsDelimitedToken(searchText, token.Key))
                {
                    return token.Value;
                }
            }

            return null;
        }

        private static string TryMatchToken(string searchText, KeyValuePair<string, string>[] tokens)
        {
            foreach (KeyValuePair<string, string> token in tokens)
            {
                if (searchText.Contains(token.Key))
                {
                    return token.Value;
                }
            }

            return null;
        }

        // HDR uses a mix of matching strategies: "hdr10+"/"hdr10"/"dolby
        // vision" are distinctive enough for plain substring matching, but
        // the short "dv" abbreviation is not - it collides with the very
        // common "DVD"/"DVDRip" release tag under naive substring matching
        // ("dvdrip".Contains("dv") is true). "dv" is only accepted as a
        // genuinely delimited, standalone token.
        private static string TryParseHdr(string searchText)
        {
            if (searchText.Contains("hdr10+"))
            {
                return "HDR10+";
            }

            if (searchText.Contains("hdr10"))
            {
                return "HDR10";
            }

            if (searchText.Contains("dolby vision"))
            {
                return "Dolby Vision";
            }

            if (ContainsDelimitedToken(searchText, "dv"))
            {
                return "Dolby Vision";
            }

            return null;
        }

        private static string TryParseAtmos(string searchText)
        {
            // "dolby atmos" already contains "atmos" as a substring, so a
            // single check covers both conventions.
            if (searchText.Contains("atmos"))
            {
                return "Atmos";
            }

            return null;
        }

        private static string TryParseContainer(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return null;
            }

            string lower = filename.ToLowerInvariant();

            foreach (KeyValuePair<string, string> extension in KnownContainerExtensions)
            {
                if (lower.EndsWith(extension.Key, StringComparison.Ordinal))
                {
                    return extension.Value;
                }
            }

            return null;
        }

        // Strips the last "." extension only, e.g.
        // "Big.Buck.Bunny.2008.1080p.mkv" -> "Big.Buck.Bunny.2008.1080p".
        // A formatting transform, not parsing - see
        // NormalizedPlaybackSource.ReleaseName.
        private static string DeriveReleaseName(string releaseFilename)
        {
            if (string.IsNullOrWhiteSpace(releaseFilename))
            {
                return null;
            }

            int lastDot = releaseFilename.LastIndexOf('.');

            if (lastDot <= 0)
            {
                return releaseFilename;
            }

            return releaseFilename.Substring(0, lastDot);
        }

        // Token-aware/delimiter-aware match for short, ambiguous tokens
        // (currently only "dv"): the token must be bounded on both sides
        // by a non-alphanumeric character or a string boundary, so e.g.
        // "Movie.2020.DV.2160p" matches but "DVDRip"/"DVD" does not (the
        // character immediately after "dv" in "dvdrip" is 'd', which is
        // alphanumeric, so the right boundary check fails).
        private static bool ContainsDelimitedToken(string haystack, string token)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(token))
            {
                return false;
            }

            int searchStart = 0;

            while (true)
            {
                int index = haystack.IndexOf(token, searchStart, StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    return false;
                }

                bool leftBoundaryOk = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
                int afterIndex = index + token.Length;
                bool rightBoundaryOk = afterIndex >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIndex]);

                if (leftBoundaryOk && rightBoundaryOk)
                {
                    return true;
                }

                searchStart = index + 1;
            }
        }

        // =========================================================
        // DETERMINISTIC SELF-CHECKS
        // =========================================================
        //
        // Hand-rolled, dependency-free checks against this pure function -
        // no test framework, no addon/network dependency. Constructs
        // synthetic StremioStream/PlaybackSource objects directly and
        // asserts the expected normalized output. Previously exercised via
        // a Settings "Run Self-Checks" button, removed from that
        // user-facing panel in a later correction pass (internal
        // diagnostic UI, not something Zino's users need) - this method
        // itself is left in place as a reusable, dependency-free
        // diagnostic entry point on the production service.

        public static List<string> RunSelfChecks()
        {
            PlaybackSourceNormalizationService normalizer = new PlaybackSourceNormalizationService();
            List<string> results = new List<string>();

            results.Add(RunCheck(
                "DV token does NOT falsely match inside DVDRip",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "720p DVDRip", null, null, null);
                    return normalized.HdrInfo == null;
                }));

            results.Add(RunCheck(
                "Delimited DV token correctly matches Dolby Vision",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "Movie.2020.DV.2160p", null, null, null);
                    return normalized.HdrInfo == "Dolby Vision";
                }));

            results.Add(RunCheck(
                "1080p token parses to ResolutionHeight 1080",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "1080p WEB-DL", null, null, null);
                    return normalized.ResolutionHeight == 1080;
                }));

            results.Add(RunCheck(
                "1440p/2K token parses to ResolutionHeight 1440",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "2K WEB-DL", null, null, null);
                    return normalized.ResolutionHeight == 1440;
                }));

            results.Add(RunCheck(
                "FHD token parses to ResolutionHeight 1080",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "FHD WEB-DL", null, null, null);
                    return normalized.ResolutionHeight == 1080;
                }));

            results.Add(RunCheck(
                "Delimited HD token parses to ResolutionHeight 720",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "Movie.2020.HD.WEB-DL", null, null, null);
                    return normalized.ResolutionHeight == 720;
                }));

            results.Add(RunCheck(
                "Bare HD token does NOT falsely match inside HDR10 (no resolution token present)",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "HDR10 WEB-DL", null, null, null);
                    return normalized.ResolutionHeight == null;
                }));

            results.Add(RunCheck(
                "2160p resolution alone does not imply HDR",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "2160p", null, null, null);
                    return normalized.HdrInfo == null;
                }));

            results.Add(RunCheck(
                "HDR10+ parses distinctly from HDR10",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "2160p HDR10+", null, null, null);
                    return normalized.HdrInfo == "HDR10+";
                }));

            results.Add(RunCheck(
                "DTS-HD parses distinctly from DTS",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "DTS-HD MA 5.1", null, null, null);
                    return normalized.AudioInfo == "DTS-HD";
                }));

            results.Add(RunCheck(
                "Dolby-branded audio codec does not imply Atmos",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "AC3 5.1", null, null, null);
                    return normalized.AtmosInfo == null;
                }));

            results.Add(RunCheck(
                "Container and ReleaseName derived from filename",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "1080p", null, null, "Big.Buck.Bunny.2008.1080p.mkv");
                    return normalized.ContainerInfo == "MKV" &&
                           normalized.ReleaseName == "Big.Buck.Bunny.2008.1080p";
                }));

            results.Add(RunCheck(
                "Title and Description remain separate, neither overwrites the other",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, null, "Original Title", "Original Description", null);
                    return normalized.Title == "Original Title" &&
                           normalized.Description == "Original Description";
                }));

            results.Add(RunCheck(
                "Unrecognised text leaves every parsed field null",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "Some Random Stream Name", null, null, null);
                    return normalized.ResolutionHeight == null &&
                           normalized.CodecInfo == null &&
                           normalized.HdrInfo == null &&
                           normalized.AtmosInfo == null &&
                           normalized.AudioInfo == null &&
                           normalized.ContainerInfo == null;
                }));

            results.Add(RunCheck(
                "AudioChannels remains deferred (always null) this slice",
                () =>
                {
                    NormalizedPlaybackSource normalized = NormalizeSynthetic(
                        normalizer, "DTS-HD 5.1", null, null, null);
                    return normalized.AudioChannels == null;
                }));

            return results;
        }

        private static NormalizedPlaybackSource NormalizeSynthetic(
            PlaybackSourceNormalizationService normalizer,
            string name,
            string title,
            string description,
            string filename)
        {
            StremioStream stream = new StremioStream
            {
                Name = name,
                Title = title,
                Description = description,
                Url = "http://example.com/video.mp4"
            };

            if (!string.IsNullOrWhiteSpace(filename))
            {
                stream.BehaviorHints = new StremioStreamBehaviorHints
                {
                    Filename = filename
                };
            }

            PlaybackSource source = new PlaybackSource
            {
                Url = stream.Url,
                Name = stream.Name,
                Title = stream.Title,
                Kind = PlaybackSourceKind.Direct,
                OriginatingStream = stream
            };

            return normalizer.Normalize(source);
        }

        private static string RunCheck(string description, Func<bool> check)
        {
            try
            {
                bool passed = check();
                return (passed ? "PASS - " : "FAIL - ") + description;
            }
            catch (Exception ex)
            {
                return "ERROR - " + description + " (" + ex.Message + ")";
            }
        }
    }
}
