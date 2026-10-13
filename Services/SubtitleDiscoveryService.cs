using Newtonsoft.Json.Linq;
using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // ADDON SUBTITLE (Slice 20 Stage 1B)
    // =============================================================
    //
    // One subtitle offered by an addon. Url is INTERNAL: subtitle links
    // can carry provider tokens, so it must never be logged, displayed or
    // persisted. A user-facing list (Stage 3) shows Language and the
    // source addon name only.
    public sealed class AddonSubtitle
    {
        public string Id { get; set; }

        // Language code exactly as the addon sent it (OpenSubtitles v3
        // sends three-letter codes such as "eng"). Mapping to display
        // names belongs to the subtitle UI stage.
        public string Language { get; set; }

        internal string Url { get; set; }

        // The provider's own short description of the entry (e.g. its
        // source and file name) when it sends one. Display text only:
        // entries whose label looks like a link are given none.
        public string Label { get; set; }

        // Safe display name of the addon that offered it - never a URL.
        public string SourceAddonName { get; set; }
    }

    // The outcome of one subtitle discovery: the combined subtitles plus
    // each addon's own outcome, so a caller can tell "no subtitles exist"
    // (every queried addon answered, with nothing) from "an addon failed".
    public sealed class SubtitleDiscoveryResult
    {
        // True when the content has no usable identifier at all, so no
        // addon could even be asked (distinct from "no addon is installed").
        public bool ContentUnidentified { get; set; }

        public List<AddonSubtitle> Subtitles { get; set; } = new List<AddonSubtitle>();

        public List<AddonQueryResult<AddonSubtitle>> PerAddon { get; set; } =
            new List<AddonQueryResult<AddonSubtitle>>();
    }

    // =============================================================
    // SUBTITLE DISCOVERY SERVICE (Slice 20 Stage 1B)
    // =============================================================
    //
    // Capability foundation for the later subtitle workflow - there is
    // no subtitle UI and no playback integration in this stage, and
    // nothing calls this yet.
    //
    // Asks every enabled addon that holds the SubtitleProvider role AND
    // whose manifest declares the "subtitles" resource for the content
    // in question, via the generic AddonResourceQuery. Compatibility
    // (movie/series type, "tt" id prefix, ...) comes from each addon's
    // own manifest declarations; nothing here names a provider.
    public static class SubtitleDiscoveryService
    {
        // Content types the Stremio "subtitles" resource is requested
        // for here. Anime episodes carry kitsu:/other ids that subtitle
        // addons declaring only "tt" simply skip, via their manifest.
        public const string MovieType = "movie";

        public const string SeriesType = "series";

        // Canonical Stremio content id for a subtitle request: the IMDb
        // id for a movie, "<imdbId>:<season>:<episode>" for a series
        // episode. Null when the inputs cannot form a valid id (unknown
        // type, missing id, or a series without both season and episode).
        public static string BuildContentId(
            string type,
            string contentId,
            int? season,
            int? episode)
        {
            if (string.IsNullOrWhiteSpace(contentId))
            {
                return null;
            }

            string trimmed = contentId.Trim();

            if (string.Equals(type, MovieType, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            if (string.Equals(type, SeriesType, StringComparison.OrdinalIgnoreCase))
            {
                if (!season.HasValue || !episode.HasValue ||
                    season.Value < 0 || episode.Value < 1)
                {
                    return null;
                }

                return trimmed + ":" + season.Value + ":" + episode.Value;
            }

            return null;
        }

        public static async Task<SubtitleDiscoveryResult> DiscoverAsync(
            IEnumerable<AddonInstance> addons,
            string type,
            string contentId,
            int? season,
            int? episode,
            CancellationToken cancellationToken)
        {
            string id = BuildContentId(type, contentId, season, episode);

            if (id == null)
            {
                return new SubtitleDiscoveryResult { ContentUnidentified = true };
            }

            return await QueryAndCollectAsync(
                addons, type.Trim().ToLowerInvariant(), id, cancellationToken);
        }

        // Slice 20 Stage 3: discovery for the content that is actually
        // playing. The playback context already carries the canonical
        // Stremio id ("tt..." for a movie, "tt...:S:E" for a series
        // episode, "kitsu:id:episode" for anime), which is exactly the id
        // the subtitles resource expects, so it is used verbatim rather than
        // rebuilt. Which addons are asked - and whether a given addon can
        // serve this id at all - is still decided by their own manifests.
        public static async Task<SubtitleDiscoveryResult> DiscoverForContentAsync(
            IEnumerable<AddonInstance> addons,
            ContentType contentType,
            string canonicalKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                return new SubtitleDiscoveryResult { ContentUnidentified = true };
            }

            string type = contentType == ContentType.Movie ? MovieType : SeriesType;

            return await QueryAndCollectAsync(
                addons, type, canonicalKey.Trim(), cancellationToken);
        }

        private static async Task<SubtitleDiscoveryResult> QueryAndCollectAsync(
            IEnumerable<AddonInstance> addons,
            string normalizedType,
            string id,
            CancellationToken cancellationToken)
        {
            SubtitleDiscoveryResult discovery = new SubtitleDiscoveryResult();

            discovery.PerAddon = await AddonResourceQuery.QueryAsync<AddonSubtitle>(
                addons,
                AddonCapabilities.Subtitles,
                AddonRole.SubtitleProvider,
                normalizedType,
                id,
                ParseStandardResponse,
                AddonResourceQuery.DefaultTimeoutMs,
                AddonResourceQuery.DefaultMaxConcurrency,
                cancellationToken);

            foreach (AddonQueryResult<AddonSubtitle> result in discovery.PerAddon)
            {
                if (result.Items == null)
                {
                    continue;
                }

                foreach (AddonSubtitle subtitle in result.Items)
                {
                    subtitle.SourceAddonName = result.AddonName;
                    discovery.Subtitles.Add(subtitle);
                }
            }

            return discovery;
        }

        // Parser for the standard Stremio "subtitles" response
        // ({"subtitles":[{"id":..., "url":..., "lang":...}, ...]}) - the
        // shape OpenSubtitles v3 returns, confirmed against its live
        // response (extra per-entry fields are ignored). A provider with
        // a different schema needs its own parser, passed to
        // AddonResourceQuery in place of this one.
        //
        // A well-formed response with no entries returns an empty list (a
        // successful empty result). A response that is not JSON, or has
        // no "subtitles" array, throws - the query reports that addon as
        // Failed rather than as "no subtitles". Entries without a usable
        // http(s) url are dropped. Never fabricates entries.
        internal static List<AddonSubtitle> ParseStandardResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new FormatException("Empty subtitles response.");
            }

            JObject root = JObject.Parse(json);

            JArray entries = root["subtitles"] as JArray;

            if (entries == null)
            {
                throw new FormatException("Response has no subtitles list.");
            }

            List<AddonSubtitle> subtitles = new List<AddonSubtitle>();

            foreach (JToken entry in entries)
            {
                JObject item = entry as JObject;

                if (item == null)
                {
                    continue;
                }

                string url = ReadString(item, "url");

                Uri uri;

                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                subtitles.Add(new AddonSubtitle
                {
                    Id = ReadString(item, "id"),
                    Language = ReadString(item, "lang"),
                    Label = SafeLabel(ReadString(item, "label")),
                    Url = url
                });
            }

            return subtitles;
        }

        private static string SafeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            string trimmed = label.Trim();

            if (trimmed.IndexOf("://", StringComparison.Ordinal) >= 0 ||
                trimmed.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            return trimmed.Length > 80 ? trimmed.Substring(0, 80) : trimmed;
        }

        private static string ReadString(JObject item, string name)
        {
            JToken token = item[name];

            if (token == null ||
                token.Type == JTokenType.Null ||
                token.Type == JTokenType.Object ||
                token.Type == JTokenType.Array)
            {
                return null;
            }

            return token.ToString();
        }
    }
}
