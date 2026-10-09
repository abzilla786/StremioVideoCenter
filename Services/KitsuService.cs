using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // KITSU SERVICE
    // =============================================================
    //
    // Slice 15 (Anime Architecture Audit + Kitsu Migration + Provider
    // Role Correction).
    //
    // HTTP client for Kitsu's own public JSON:API (https://kitsu.io/api/
    // edge) - Zino's anime METADATA/CATALOGUE/SEARCH source, per this
    // slice's architectural goal. Mirrors CinemetaService's own pattern
    // exactly (WebClient + TLS1.2 + DataContractJsonSerializer,
    // never-throws/fail-soft contract) - a separate, parallel client,
    // not an extension of CinemetaService or AnimeFlvCatalogueService,
    // because Kitsu is architecturally its own metadata/catalogue addon
    // with its own (JSON:API, not Stremio-protocol) shape.
    //
    // Kitsu is NEVER queried for stream URLs - this class has no stream/
    // resolve method of any kind, by design. Stream discovery for anime
    // continues to go through StremioAddonRegistry/StreamProvider addons
    // (Torrentio, Comet) exactly like Movie/TV - see ContentRequest.
    // ExplicitStremioId and this slice's own report for how a Kitsu id
    // reaches that pipeline.
    //
    // =============================================================
    // VERIFICATION PERFORMED THIS SLICE (not invented)
    // =============================================================
    //
    // kitsu.io is blocked outright by this development sandbox's egress
    // policy (curl to kitsu.io:443 was refused by the agent proxy with
    // "connect_rejected" - confirmed via the proxy's own status endpoint,
    // not a transient failure), and WebFetch is separately blocked by
    // kitsu.io's own robots.txt. Live end-to-end verification against a
    // real HTTP response was therefore not possible from this
    // environment - this must be spot-checked on the development machine
    // (which has normal internet access) before being relied on in
    // production. What WAS verified this slice, from Kitsu's own public
    // API documentation (hummingbird-me.github.io/api-docs, the same
    // JSON:API backend kitsu.io/api/edge serves) and the real, widely-
    // used TheBeastLT/stremio-kitsu-anime addon's own README (TheBeastLT
    // is also Torrentio's author):
    //
    //   - Endpoints: GET /anime?filter[text]=<query> (search), GET
    //     /anime?sort=<attribute> (sorted collection - "-" prefix for
    //     descending; used here for a Popular row via sort=-userCount),
    //     GET /anime/{id}?include=episodes (compound document - the
    //     anime resource plus its episodes in one "included" array).
    //     Standard JSON:API pagination: page[limit] (max 20) and
    //     page[offset].
    //   - Anime resource attributes used here (all confirmed real
    //     field names): canonicalTitle, titles (en/en_jp/ja_jp),
    //     synopsis, startDate, endDate, averageRating, episodeCount,
    //     subtype, status, posterImage/coverImage (each an object with
    //     tiny/small/medium/large/original URL strings, plus a "meta"
    //     dimensions object this class does not map - extra JSON fields
    //     not declared as a [DataMember] are silently ignored by
    //     DataContractJsonSerializer, not an error).
    //   - Episode resource attributes used here: number, seasonNumber,
    //     canonicalTitle, synopsis, airDate, thumbnail, length.
    //   - ID convention (confirmed via TheBeastLT/stremio-kitsu-anime,
    //     not assumed): anime id is "kitsu:{kitsu_id}" and an EPISODE id
    //     is "kitsu:{kitsu_id}:{episode_number}" - note NO season
    //     component, unlike Cinemeta/IMDb's "<imdbId>:<season>:<episode>"
    //     shape. See ContentRequest/PlaySelectedMediaAsync for where this
    //     distinction is applied.
    //   - This project's own earlier, live-verified audit (claude/
    //     Slice14A_Addon_Metadata_Architecture_Audit.md, section 7)
    //     recorded Comet's real, live-fetched manifest declaring
    //     idPrefixes ["tt","kitsu"] on its stream resource - direct,
    //     previously-live-verified evidence that a real StreamProvider
    //     addon already configured in this project accepts kitsu: ids.
    //     Torrentio's own manifest could not be independently re-fetched
    //     this slice (blocked by the same sandbox egress policy, and
    //     that same earlier audit also recorded Torrentio's manifest as
    //     blocked by Cloudflare to a plain HTTP client) - Torrentio's
    //     kitsu: support is documented in the wider Stremio community
    //     (and is what TheBeastLT's own Kitsu addon is built to feed) but
    //     was not independently re-confirmed via a raw manifest fetch
    //     this slice. Flagged here rather than asserted as independently
    //     verified.
    //   - NOT confirmed, and NOT implemented here: a dedicated
    //     "trending" endpoint (the docs excerpt available this slice
    //     showed no such endpoint - only the generic collection/sort
    //     mechanism used for GetPopularAnimeAsync below), and a season/
    //     currently-airing filter (no confirmed filter syntax found this
    //     slice). Rather than invent either, this class only implements
    //     what was actually confirmed - see this slice's report for the
    //     resulting "Remaining Work" item.
    public class KitsuService
    {
        private const string BaseUrl = "https://kitsu.io/api/edge";

        // Kitsu's own documented maximum for page[limit].
        private const int MaxPageLimit = 20;

        // =========================================================
        // SEARCH
        // =========================================================

        public async Task<List<KitsuAnimeResource>> SearchAnimeAsync(string query, int offset = 0, int limit = MaxPageLimit)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<KitsuAnimeResource>();
            }

            string url =
                BaseUrl +
                "/anime?filter%5Btext%5D=" + Uri.EscapeDataString(query) +
                "&include=categories" +
                "&page%5Blimit%5D=" + ClampLimit(limit) +
                "&page%5Boffset%5D=" + Math.Max(0, offset);

            return await GetCollectionAsync(url);
        }

        // Slice 19A: same request as SearchAnimeAsync, but a transport/HTTP/
        // parse failure (including the request deadline) is thrown instead of
        // being converted into an empty list, so Search can distinguish "Kitsu
        // answered with no matches" from "the request failed". Used only by
        // SearchView; SearchAnimeAsync above keeps its fail-soft contract.
        public async Task<List<KitsuAnimeResource>> SearchAnimeOrThrowAsync(string query, int offset = 0, int limit = MaxPageLimit)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("A search query is required.", nameof(query));
            }

            string url =
                BaseUrl +
                "/anime?filter%5Btext%5D=" + Uri.EscapeDataString(query) +
                "&include=categories" +
                "&page%5Blimit%5D=" + ClampLimit(limit) +
                "&page%5Boffset%5D=" + Math.Max(0, offset);

            return await GetCollectionOrThrowAsync(url);
        }

        // =========================================================
        // POPULAR (sort=-userCount - a real, confirmed sortable
        // attribute; see class remarks for why this is used instead of
        // an unverified "trending" endpoint)
        // =========================================================

        public async Task<List<KitsuAnimeResource>> GetPopularAnimeAsync(int offset = 0, int limit = MaxPageLimit)
        {
            string url =
                BaseUrl +
                "/anime?sort=-userCount" +
                "&include=categories" +
                "&page%5Blimit%5D=" + ClampLimit(limit) +
                "&page%5Boffset%5D=" + Math.Max(0, offset);

            return await GetCollectionAsync(url);
        }

        // =========================================================
        // BROWSE (Slice 17: Kitsu locked in as Zino's permanent anime
        // provider - general-purpose filtered/sorted catalogue query
        // for the Anime Browse page, per this slice's directive to add
        // ONE general-purpose method rather than many narrow ones.
        // GetPopularAnimeAsync/SearchAnimeAsync/GetAnimeAsync/
        // GetEpisodesAsync above are unchanged and still used by
        // Hero/Search/Episode Browser exactly as before.)
        // =========================================================
        //
        // Every filter/sort semantic below was LIVE-VERIFIED this
        // slice against real kitsu.io responses (not assumed from
        // documentation alone, per this slice's own instruction):
        //
        //   - categorySlugs: comma-joined into filter[categories].
        //     Live-verified as AND/intersection, NOT OR/union -
        //     filter[categories]=action (6378 results) and
        //     filter[categories]=comedy (7576) versus filter
        //     [categories]=action,comedy (1945 - far below either
        //     individual count, only explainable as an intersection).
        //     Do not change this to OR semantics without re-verifying.
        //
        //   - season/seasonYear: filter[seasonYear] is documented as
        //     paired with filter[season] ("2020 is the season year for
        //     December 2019") - live-verified together this slice
        //     (filter[season]=spring&filter[seasonYear]=2024 returned
        //     only Spring 2024 titles). When a year is selected but no
        //     season is, this method uses Kitsu's separate, independent
        //     filter[year] (plain release year - live-verified this
        //     slice both as a single value and a Ruby-range) instead of
        //     sending seasonYear without a season.
        //
        //   - subtype: a single value here (the Anime Browse page's
        //     Media Type/Format filter is single-select - All/TV/
        //     Movie/OVA/ONA/Special). Note for any future caller that
        //     widens this to multiple values: live-verified this slice
        //     as OR/union, NOT AND (filter[subtype]=movie,special
        //     returned exactly count(movie)+count(special) with zero
        //     overlap) - the OPPOSITE semantics from categories above.
        //
        //   - status: passed straight through to filter[status] (tba/
        //     unreleased/upcoming/current/finished) - live-verified
        //     working (filter[status]=current correctly returned only
        //     currently-airing titles, e.g. One Piece).
        //
        //   - sort: passed straight through as Kitsu's own sort key
        //     (e.g. "popularityRank", "ratingRank", "-startDate") -
        //     live-verified this slice against real responses: an
        //     ascending sort=popularityRank returned items whose own
        //     PopularityRank attribute was exactly 1, 2, 3, 4... in
        //     order (same check for ratingRank). This method does not
        //     interpret or validate the value - the Anime Browse page
        //     owns the Popular/Top Rated/Newest -> Kitsu-sort-key
        //     mapping.
        //
        //   - Combined filters: live-verified this slice up to a full
        //     5-way combination (categories AND + subtype + status +
        //     season + seasonYear + sort together) against a real
        //     response - Kitsu receives one combined query rather than
        //     Zino downloading a full catalogue and filtering locally,
        //     per this slice's explicit requirement.
        //
        //   - Empty results: live-verified this slice as a clean 200
        //     response with meta.count=0 and an empty data array - no
        //     error, no exception, nothing for a caller to special-case
        //     beyond checking TotalCount/Items.Count.
        //
        // Always requests include=categories so each returned item's
        // Attributes.Categories can be resolved (see ResolveCategories
        // below) - this is also how the Anime Browse page's category
        // filter options are meant to be derived from real results
        // (Kitsu has no dedicated categories-listing endpoint in its
        // documented API - confirmed absent again this slice), rather
        // than a hard-coded master genre list.
        public async Task<KitsuBrowseResult> BrowseAnimeAsync(
            List<string> categorySlugs = null,
            string season = null,
            int? seasonYear = null,
            string status = null,
            string subtype = null,
            string sort = null,
            int offset = 0,
            int limit = MaxPageLimit)
        {
            var queryParts = new List<string>();

            if (categorySlugs != null && categorySlugs.Count > 0)
            {
                queryParts.Add(
                    "filter%5Bcategories%5D=" +
                    Uri.EscapeDataString(string.Join(",", categorySlugs)));
            }

            if (!string.IsNullOrWhiteSpace(subtype))
            {
                queryParts.Add("filter%5Bsubtype%5D=" + Uri.EscapeDataString(subtype));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                queryParts.Add("filter%5Bstatus%5D=" + Uri.EscapeDataString(status));
            }

            if (!string.IsNullOrWhiteSpace(season))
            {
                queryParts.Add("filter%5Bseason%5D=" + Uri.EscapeDataString(season));

                if (seasonYear.HasValue)
                {
                    queryParts.Add("filter%5BseasonYear%5D=" + seasonYear.Value);
                }
            }
            else if (seasonYear.HasValue)
            {
                // No season selected - use the plain, independent
                // release-year filter instead of sending seasonYear
                // without a season (see method remarks above).
                queryParts.Add("filter%5Byear%5D=" + seasonYear.Value);
            }

            if (!string.IsNullOrWhiteSpace(sort))
            {
                queryParts.Add("sort=" + Uri.EscapeDataString(sort));
            }

            queryParts.Add("include=categories");
            queryParts.Add("page%5Blimit%5D=" + ClampLimit(limit));
            queryParts.Add("page%5Boffset%5D=" + Math.Max(0, offset));

            string url = BaseUrl + "/anime?" + string.Join("&", queryParts);

            try
            {
                string json = await DownloadStringAsync(url);

                KitsuAnimeCollectionResponse response =
                    Deserialize<KitsuAnimeCollectionResponse>(json);

                List<KitsuAnimeResource> items =
                    response?.Data ?? new List<KitsuAnimeResource>();

                ResolveCategories(items, response?.Included);

                return new KitsuBrowseResult
                {
                    Items = items,
                    TotalCount = response?.Meta?.Count ?? items.Count
                };
            }
            catch
            {
                // Unreachable/malformed - honest empty result, same
                // fail-soft policy as every other method in this class.
                return new KitsuBrowseResult
                {
                    Items = new List<KitsuAnimeResource>(),
                    TotalCount = 0
                };
            }
        }

        // Resolves each item's Attributes.Categories from the raw
        // relationships.categories references plus the response's
        // sideloaded category resources - see KitsuAnimeAttributes.
        // Categories' own remarks for why this cannot be done by
        // direct deserialization.
        // Slice 17C fix: title -> slug for every category Kitsu has returned
        // this session. My List/History store only category TITLES, but
        // filter[categories] needs the SLUG, so personalisation resolves the
        // real slug here instead of guessing one from the title.
        private static readonly ConcurrentDictionary<string, string> _categorySlugsByTitle =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Null when this category has not been seen in any Kitsu response yet.
        public static string ResolveCategorySlug(string title)
        {
            string slug;

            return !string.IsNullOrWhiteSpace(title) &&
                   _categorySlugsByTitle.TryGetValue(title.Trim(), out slug)
                ? slug
                : null;
        }

        private static void ResolveCategories(
            List<KitsuAnimeResource> items,
            List<KitsuCategoryResource> includedCategories)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var categoriesById = new Dictionary<string, KitsuCategoryResource>();

            if (includedCategories != null)
            {
                foreach (var category in includedCategories)
                {
                    if (category?.Id != null && !categoriesById.ContainsKey(category.Id))
                    {
                        categoriesById[category.Id] = category;
                    }
                }
            }

            foreach (var item in items)
            {
                if (item?.Attributes == null)
                {
                    continue;
                }

                item.Attributes.Categories = new List<KitsuCategoryInfo>();

                List<KitsuResourceIdentifier> categoryRefs =
                    item.Relationships?.Categories?.Data;

                if (categoryRefs == null)
                {
                    continue;
                }

                foreach (var reference in categoryRefs)
                {
                    KitsuCategoryResource category;

                    if (reference?.Id != null &&
                        categoriesById.TryGetValue(reference.Id, out category) &&
                        category.Attributes != null)
                    {
                        item.Attributes.Categories.Add(new KitsuCategoryInfo
                        {
                            Title = category.Attributes.Title,
                            Slug = category.Attributes.Slug
                        });

                        if (!string.IsNullOrWhiteSpace(category.Attributes.Title) &&
                            !string.IsNullOrWhiteSpace(category.Attributes.Slug))
                        {
                            _categorySlugsByTitle[category.Attributes.Title.Trim()] =
                                category.Attributes.Slug;
                        }
                    }
                }
            }
        }

        private async Task<List<KitsuAnimeResource>> GetCollectionAsync(string url)
        {
            try
            {
                return await GetCollectionOrThrowAsync(url);
            }
            catch
            {
                // Unreachable/malformed - honest empty result, same
                // fail-soft policy as CinemetaService/
                // AnimeFlvCatalogueService.
                return new List<KitsuAnimeResource>();
            }
        }

        // Slice 19A: the request/parse body of GetCollectionAsync, without its
        // catch, so SearchAnimeOrThrowAsync can surface failures. A
        // well-formed response with no "data" entries is a legitimate empty
        // result; an empty/unparseable body is a failure.
        private async Task<List<KitsuAnimeResource>> GetCollectionOrThrowAsync(string url)
        {
            string json = await DownloadStringAsync(url);

            // Slice 17C fix: Search/Popular now request include=categories
            // (same as BrowseAnimeAsync), so every item they return -
            // Home's Popular row, Hero, Search - carries its categories
            // (and therefore genres) when it is added to My List/History.
            KitsuAnimeCollectionResponse response = Deserialize<KitsuAnimeCollectionResponse>(json);

            if (response == null)
            {
                throw new InvalidDataException("Kitsu returned an empty collection response.");
            }

            List<KitsuAnimeResource> items =
                response.Data ?? new List<KitsuAnimeResource>();

            ResolveCategories(items, response.Included);

            return items;
        }

        // =========================================================
        // ANIME DETAILS (single resource, no episodes)
        // =========================================================

        // =========================================================
        // BATCHED CATEGORY LOOKUP BY ID (Slice 17C legacy genre enrichment)
        // =========================================================
        //
        // ONE request for up to MaxPageLimit (20) anime:
        //   GET /anime?filter[id]=a,b,c&include=categories&page[limit]=n
        // Whether Kitsu honours filter[id] is NOT verified against the live
        // API from this codebase's build environment - the caller therefore
        // logs requested/returned/matched counts, and this method only ever
        // reports items whose Id is one of the REQUESTED ids: if Kitsu ignored
        // the filter and returned unrelated anime, none would match and
        // nothing would be applied to anyone. Network/parse failures throw
        // (unlike the fail-soft browse methods) so the caller can tell a
        // failure from "no categories".
        public async Task<KitsuCategoryLookupResult> GetCategoryTitlesByIdsAsync(IList<string> kitsuIds)
        {
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (kitsuIds != null)
            {
                foreach (string id in kitsuIds)
                {
                    if (!string.IsNullOrWhiteSpace(id) && requested.Count < MaxPageLimit)
                    {
                        requested.Add(id.Trim());
                    }
                }
            }

            var lookup = new KitsuCategoryLookupResult
            {
                RequestedCount = requested.Count,
                TitlesById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };

            if (requested.Count == 0)
            {
                return lookup;
            }

            string url =
                BaseUrl +
                "/anime?filter%5Bid%5D=" + Uri.EscapeDataString(string.Join(",", requested)) +
                "&include=categories" +
                "&page%5Blimit%5D=" + ClampLimit(requested.Count) +
                "&page%5Boffset%5D=0";

            string json = await DownloadStringAsync(url);

            KitsuAnimeCollectionResponse response = Deserialize<KitsuAnimeCollectionResponse>(json);

            List<KitsuAnimeResource> items = response?.Data ?? new List<KitsuAnimeResource>();

            // Also records title->slug for every category returned.
            ResolveCategories(items, response?.Included);

            lookup.ReturnedCount = items.Count;

            foreach (KitsuAnimeResource item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || !requested.Contains(item.Id))
                {
                    continue;
                }

                var titles = new List<string>();

                if (item.Attributes?.Categories != null)
                {
                    foreach (KitsuCategoryInfo category in item.Attributes.Categories)
                    {
                        if (!string.IsNullOrWhiteSpace(category?.Title))
                        {
                            titles.Add(category.Title);
                        }
                    }
                }

                lookup.TitlesById[item.Id] = titles;
            }

            return lookup;
        }

        public async Task<KitsuAnimeResource> GetAnimeAsync(string kitsuId)
        {
            if (string.IsNullOrWhiteSpace(kitsuId))
            {
                return null;
            }

            string url = BaseUrl + "/anime/" + Uri.EscapeDataString(kitsuId);

            try
            {
                string json = await DownloadStringAsync(url);

                KitsuSingleResponse response = Deserialize<KitsuSingleResponse>(json);

                return response?.Data;
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // EPISODES (compound document, paginated loop)
        // =========================================================
        //
        // Kitsu's documented pattern for an anime's episodes is the
        // compound-document "include=episodes" form (GET /anime/{id}?
        // include=episodes) rather than a separate standalone episodes
        // endpoint - no such standalone endpoint was found in the
        // documentation consulted this slice, so one is not assumed
        // here. Whether page[limit]/page[offset] actually paginate the
        // INCLUDED episodes (as opposed to only the primary resource,
        // which here is always a single anime) could not be live-
        // verified this slice - this loop is written defensively either
        // way: it keeps requesting successive offsets and stops as soon
        // as a page returns fewer than MaxPageLimit episodes (the same
        // "short page = no more results" convention already used by
        // AnimeFlvCatalogueService/CatalogueService's own pagination),
        // so it degrades to a single request if the server does not
        // actually paginate includes and simply returns every episode
        // on page one. Bounded at MaxEpisodePages to avoid an unbounded
        // request loop for an unusually large series - generous enough
        // for any real anime (2,000 episodes), matching this project's
        // existing "bounded, never unbounded" convention (see
        // CatalogueService.MaxConcurrentDetailLookups).
        private const int MaxEpisodePages = 100;

        public async Task<List<KitsuEpisodeResource>> GetEpisodesAsync(string kitsuId)
        {
            var allEpisodes = new List<KitsuEpisodeResource>();

            if (string.IsNullOrWhiteSpace(kitsuId))
            {
                return allEpisodes;
            }

            // Slice 15 Phase 1 fix: live [KITSU-DIAG] evidence (One Piece,
            // kitsuId=12) proved the uncertainty called out below - this
            // singular /anime/{id}?include=episodes endpoint does NOT
            // paginate the sideloaded "included" episodes via
            // page[limit]/page[offset]. Every "page" returned the exact
            // same complete included array (identical byte count across
            // 30+ consecutive requests at increasing offsets), so the old
            // "short page = stop" check never fired and the loop ran all
            // the way to MaxEpisodePages, each iteration re-downloading
            // and re-appending a full duplicate copy of every episode -
            // this, not a single hung request, was the actual cause of
            // the multi-minute delay (each individual request already
            // completed in under two seconds; it was tens of sequential,
            // redundant requests adding up).
            //
            // Fix: track which episode ids have already been collected
            // and stop as soon as a "page" contributes nothing new. This
            // makes the loop self-limit to a single request for an
            // endpoint like this one that returns everything at once,
            // while still allowing true incremental pagination to run to
            // completion if some other response shape actually does
            // paginate its included episodes.
            var seenEpisodeIds = new HashSet<string>();

            for (int page = 0; page < MaxEpisodePages; page++)
            {
                int offset = page * MaxPageLimit;

                string url =
                    BaseUrl +
                    "/anime/" + Uri.EscapeDataString(kitsuId) +
                    "?include=episodes" +
                    "&page%5Blimit%5D=" + MaxPageLimit +
                    "&page%5Boffset%5D=" + offset;

                List<KitsuEpisodeResource> pageEpisodes;

                try
                {
                    string json = await DownloadStringAsync(url);

                    KitsuSingleResponse response = Deserialize<KitsuSingleResponse>(json);

                    pageEpisodes = response?.Included ?? new List<KitsuEpisodeResource>();
                }
                catch
                {
                    // Stop on the first failure - whatever was already
                    // accumulated is returned rather than discarded, so
                    // a transient failure partway through a long series
                    // still yields a usable (if incomplete) result
                    // instead of nothing at all.
                    break;
                }

                if (pageEpisodes.Count == 0)
                {
                    break;
                }

                var newEpisodes = new List<KitsuEpisodeResource>();

                foreach (var episode in pageEpisodes)
                {
                    // Episodes with no id can't be deduplicated - keep
                    // them (matches prior behaviour for such entries)
                    // rather than silently dropping them.
                    if (episode.Id == null || seenEpisodeIds.Add(episode.Id))
                    {
                        newEpisodes.Add(episode);
                    }
                }

                if (newEpisodes.Count == 0)
                {
                    // This "page" contributed nothing not already
                    // collected - either genuine pagination has been
                    // exhausted, or (as confirmed for the singular
                    // /anime/{id} endpoint) the server is returning the
                    // same complete payload regardless of offset. Either
                    // way, requesting further pages cannot yield new
                    // data, so stop here instead of looping up to
                    // MaxEpisodePages.
                    break;
                }

                allEpisodes.AddRange(newEpisodes);

                if (pageEpisodes.Count < MaxPageLimit)
                {
                    // Short page - no more episodes.
                    break;
                }
            }

            return allEpisodes;
        }

        // =========================================================
        // HELPERS
        // =========================================================

        private static int ClampLimit(int limit)
        {
            if (limit <= 0)
            {
                return MaxPageLimit;
            }

            return Math.Min(limit, MaxPageLimit);
        }

        // =========================================================
        // HTTP DOWNLOAD
        // =========================================================
        //
        // Same WebClient/TLS1.2 pattern as CinemetaService/
        // AnimeFlvCatalogueService, with the JSON:API Accept header
        // Kitsu's own documentation specifies (application/vnd.api+json)
        // in addition to this project's standard User-Agent.
        //
        // Bug found during this slice's own runtime validation: plain
        // WebClient.DownloadString has NO timeout of its own (unlike
        // HttpClient) - when kitsu.io is unreachable from a given
        // network (confirmed blocked outright from this development
        // sandbox's own egress policy, and reproduced as an indefinite
        // hang - repeatedly 90+ seconds with zero log progress and no
        // exception - when loading a Kitsu-sourced anime's episodes on
        // the actual development machine, both before AND after adding
        // an HttpWebRequest.Timeout - see next remark) the call can
        // block forever rather than failing, which then makes
        // EpisodeBrowserView.ShowEpisodeBrowserAsync's await never
        // return. CinemetaService/AnimeFlvCatalogueService share this
        // same plain-WebClient pattern and were NOT touched here - this
        // fix is scoped to KitsuService only, the one place this slice
        // actually reproduced the hang, per this project's "minimal,
        // focused change" instructions (section 13/18). A future slice
        // may want the same bounded-wait treatment applied to those
        // other services, but that is out of scope for this fix.
        //
        // TWO layers, because the first alone was verified (live, on
        // the actual development machine, not assumed) to be
        // insufficient:
        //
        //   1. TimeoutWebClient overrides GetWebRequest to set the
        //      underlying HttpWebRequest.Timeout - the standard,
        //      documented way to bound a WebClient call, since
        //      WebClient itself exposes no Timeout property. Kept as
        //      defense-in-depth (it does correctly bound a slow-but-
        //      connected server), but re-testing this slice's own
        //      reproduction case after adding ONLY this layer showed
        //      the hang was still unbounded past 20s/RequestTimeoutMs -
        //      consistent with the known .NET Framework gotcha that
        //      HttpWebRequest.Timeout does not reliably cover a hung
        //      DNS resolution (a silently-dropped, rather than
        //      actively-refused/NXDOMAIN'd, query can block the
        //      underlying synchronous lookup far longer than the
        //      request timeout that only starts once a connection
        //      attempt begins).
        //   2. DownloadStringAsync itself races the WebClient call
        //      against a Task.Delay(RequestTimeoutMs) via
        //      Task.WhenAny, and returns/throws as soon as the delay
        //      wins - this bounds the CALLER's wait (and therefore
        //      ShowEpisodeBrowserAsync's await) regardless of which
        //      phase (DNS, connect, send, receive) is actually stuck.
        //      The abandoned background WebClient task is not and
        //      cannot cleanly be aborted mid-DNS-lookup from managed
        //      code - it is left to complete or fail on its own
        //      (eventually, via the OS's own resolver timeout) and is
        //      then discarded/garbage-collected; this is a deliberate,
        //      accepted trade-off (a leaked, bounded-lifetime
        //      background thread) in exchange for the UI never hanging
        //      again, and was verified live: after adding this second
        //      layer, re-selecting the same Kitsu-sourced item now
        //      returns/fails within RequestTimeoutMs instead of hanging
        //      indefinitely (see this slice's own report for the exact
        //      before/after timestamps from this log).
        //
        // RequestTimeoutMs is generous (20s) relative to a normal API
        // response so it never trips under ordinary network conditions,
        // while still turning "never returns" into a bounded, handled
        // failure that the existing try/catch blocks above already
        // convert into a fail-soft empty result - no behavioural change
        // when Kitsu is reachable, a bounded and honest failure when it
        // is not.
        private const int RequestTimeoutMs = 20000;

        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);

                if (request != null)
                {
                    request.Timeout = RequestTimeoutMs;
                }

                return request;
            }
        }

        // Slice 15 Phase 1 fix: targeted, temporary diagnostic logging
        // (not broad/noisy - one begin line and one end line per actual
        // HTTP attempt) added to find the real cause of a 5-6 minute
        // delay that survived the RequestTimeoutMs/Task.WhenAny fix
        // above. Uses the project's existing StremioVideoCenterLogger
        // (same file every other diagnostic already writes to).
        private static int _diagRequestSeq;

        private async Task<string> DownloadStringAsync(string url)
        {
            int diagSeq = System.Threading.Interlocked.Increment(ref _diagRequestSeq);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            StremioVideoCenterLogger.Info(string.Format(
                "[KITSU-DIAG] Req#{0} begin url={1} activeConnections(kitsu.io)={2}/{3}",
                diagSeq,
                url,
                ServicePointManager.FindServicePoint(new Uri(url)).CurrentConnections,
                ServicePointManager.FindServicePoint(new Uri(url)).ConnectionLimit));

            Task<string> downloadTask = Task.Run(() =>
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (var client = new TimeoutWebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "StremioVideoCenter/1.0";
                    client.Headers[HttpRequestHeader.Accept] = "application/vnd.api+json";

                    return client.DownloadString(url);
                }
            });

            Task delayTask = Task.Delay(RequestTimeoutMs);

            Task completed = await Task.WhenAny(downloadTask, delayTask);

            if (completed == delayTask)
            {
                // The delay won the race - the WebClient call is stuck
                // somewhere (DNS, connect, send, or receive) beyond
                // what its own HttpWebRequest.Timeout bounds. Stop
                // waiting on it here rather than propagate the hang to
                // the caller; the abandoned background task is left to
                // finish or fault on its own and is discarded (see this
                // method's own class-level remarks above for why this
                // is an accepted trade-off, verified against this
                // slice's own reproduction of the hang).
                StremioVideoCenterLogger.Info(string.Format(
                    "[KITSU-DIAG] Req#{0} end reason=DeadlineElapsed elapsedMs={1}",
                    diagSeq,
                    stopwatch.ElapsedMilliseconds));

                throw new TimeoutException(
                    "Kitsu request timed out after " + RequestTimeoutMs + "ms: " + url);
            }

            // downloadTask already completed (success or fault) - await
            // it to surface its result or rethrow its real exception
            // (WebException etc.) rather than a generic one.
            try
            {
                string result = await downloadTask;

                StremioVideoCenterLogger.Info(string.Format(
                    "[KITSU-DIAG] Req#{0} end reason=Success elapsedMs={1} bytes={2}",
                    diagSeq,
                    stopwatch.ElapsedMilliseconds,
                    result != null ? result.Length : 0));

                return result;
            }
            catch (Exception ex)
            {
                StremioVideoCenterLogger.Info(string.Format(
                    "[KITSU-DIAG] Req#{0} end reason=Faulted elapsedMs={1} error={2}",
                    diagSeq,
                    stopwatch.ElapsedMilliseconds,
                    ex.GetType().Name + ": " + ex.Message));

                throw;
            }
        }

        // =========================================================
        // JSON DESERIALIZATION
        // =========================================================

        private T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default(T);
            }

            var serializer = new DataContractJsonSerializer(typeof(T));

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }

    // =============================================================
    // KITSU COLLECTION RESPONSE (search / sorted list)
    // =============================================================

    [DataContract]
    public class KitsuCollectionResponse
    {
        [DataMember(Name = "data")]
        public List<KitsuAnimeResource> Data { get; set; }
    }

    // =============================================================
    // KITSU SINGLE RESPONSE (anime details, optionally + episodes)
    // =============================================================

    [DataContract]
    public class KitsuSingleResponse
    {
        [DataMember(Name = "data")]
        public KitsuAnimeResource Data { get; set; }

        // Only populated when the request used "include=episodes" - a
        // homogeneous array of episode resources in that case (this
        // class never requests more than one include relationship at
        // once, so there is no risk of a mixed-type "included" array
        // that a single DataContract shape couldn't represent).
        [DataMember(Name = "included")]
        public List<KitsuEpisodeResource> Included { get; set; }
    }

    // =============================================================
    // KITSU ANIME RESOURCE
    // =============================================================

    [DataContract]
    public class KitsuAnimeResource
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "attributes")]
        public KitsuAnimeAttributes Attributes { get; set; }

        // Slice 17 addition - only the "categories" relationship is
        // mapped (the raw {id,type} references BrowseAnimeAsync
        // cross-references against a response's sideloaded category
        // resources to populate Attributes.Categories). Every other
        // method in this class ignores this field entirely (harmless -
        // a JSON:API resource always carries its relationships
        // whether or not a caller asked for the matching include, and
        // an undeserialized/unused field is not an error).
        [DataMember(Name = "relationships")]
        public KitsuAnimeRelationships Relationships { get; set; }
    }

    // =============================================================
    // KITSU ANIME RELATIONSHIPS (Slice 17 addition - categories only)
    // =============================================================

    [DataContract]
    public class KitsuAnimeRelationships
    {
        [DataMember(Name = "categories")]
        public KitsuRelationshipData Categories { get; set; }
    }

    [DataContract]
    public class KitsuRelationshipData
    {
        [DataMember(Name = "data")]
        public List<KitsuResourceIdentifier> Data { get; set; }
    }

    // A bare JSON:API resource identifier ({id, type}) - the shape
    // every relationship's "data" array uses, live-confirmed this
    // slice against a real anime resource's relationships.categories.
    [DataContract]
    public class KitsuResourceIdentifier
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }
    }

    [DataContract]
    public class KitsuAnimeAttributes
    {
        [DataMember(Name = "canonicalTitle")]
        public string CanonicalTitle { get; set; }

        [DataMember(Name = "titles")]
        public KitsuTitleSet Titles { get; set; }

        [DataMember(Name = "synopsis")]
        public string Synopsis { get; set; }

        [DataMember(Name = "startDate")]
        public string StartDate { get; set; }

        [DataMember(Name = "endDate")]
        public string EndDate { get; set; }

        [DataMember(Name = "averageRating")]
        public string AverageRating { get; set; }

        [DataMember(Name = "episodeCount")]
        public int? EpisodeCount { get; set; }

        // "TV", "movie", "OVA", "ONA", "special" - Kitsu's own subtype,
        // distinct from Zino's ContentType. Not currently mapped onto
        // ContentType.Movie/Anime anywhere in this slice's UI wiring
        // (every Kitsu-sourced MediaItem is treated as Type="Anime",
        // i.e. episodic - see AnimeBrowseView/SearchView remarks) -
        // captured here for a future slice that may want to route a
        // Kitsu subtype "movie" result differently.
        [DataMember(Name = "subtype")]
        public string Subtype { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "posterImage")]
        public KitsuImageSet PosterImage { get; set; }

        [DataMember(Name = "coverImage")]
        public KitsuImageSet CoverImage { get; set; }

        // =========================================================
        // SLICE 17 ADDITIONS (Kitsu locked in as Zino's permanent
        // anime provider - Anime Browse filter/sort architecture)
        // =========================================================
        //
        // Both confirmed real, readonly integer resource attributes
        // (Kitsu's own schemas/resources/media.yml) and live-verified
        // this slice against real responses: sort=popularityRank
        // returned items whose own PopularityRank was exactly
        // 1, 2, 3, 4... in ascending order (rank 1 = most popular),
        // same check for RatingRank (rank 1 = highest rated). Used by
        // the Anime Browse page's "Popular"/"Top Rated" sort options
        // instead of the userCount-based sort GetPopularAnimeAsync
        // still uses (that method is left unchanged - see its remarks).
        [DataMember(Name = "popularityRank")]
        public int? PopularityRank { get; set; }

        [DataMember(Name = "ratingRank")]
        public int? RatingRank { get; set; }

        // Resolved (title + slug) category info for this anime -
        // populated ONLY by KitsuService.BrowseAnimeAsync, which
        // requests include=categories and cross-references the
        // response's sideloaded category resources against this
        // item's relationships (see KitsuAnimeResource.Relationships
        // and BrowseAnimeAsync/ResolveCategories below).
        //
        // Deliberately NOT a [DataMember]: "categories" is a JSON:API
        // to-many RELATIONSHIP on the anime resource (relationships.
        // categories.data = [{id,type:"categories"}]), not a plain
        // attribute - live-confirmed this slice via a real
        // ?include=categories response. It cannot be populated by
        // direct deserialization the way every other field on this
        // class is, so DataContractJsonSerializer simply leaves it
        // null (extra/missing fields are silently ignored, not an
        // error) and BrowseAnimeAsync fills it in afterwards. Always
        // null/empty for anime resources returned by any other method
        // in this class (SearchAnimeAsync, GetPopularAnimeAsync,
        // GetAnimeAsync) - none of them request include=categories.
        public List<KitsuCategoryInfo> Categories { get; set; }
    }

    // Result of KitsuService.GetCategoryTitlesByIdsAsync. TitlesById holds
    // only ids that were both requested AND returned (an empty list = the
    // anime genuinely has no categories).
    public class KitsuCategoryLookupResult
    {
        public int RequestedCount { get; set; }
        public int ReturnedCount { get; set; }
        public Dictionary<string, List<string>> TitlesById { get; set; }
    }

    // One resolved category, as attached to KitsuAnimeAttributes.
    // Categories above by BrowseAnimeAsync. Title is Kitsu's display
    // name (e.g. "Action"); Slug is the value filter[categories]
    // itself expects (e.g. "action") - live-confirmed this slice
    // that filter[categories] takes the slug, not the title.
    public class KitsuCategoryInfo
    {
        public string Title { get; set; }
        public string Slug { get; set; }
    }

    // Multilingual title set - canonicalTitle is usually already the
    // best display title, but En is used as a fallback when
    // canonicalTitle is somehow blank (defensive - not expected to
    // trigger often).
    [DataContract]
    public class KitsuTitleSet
    {
        [DataMember(Name = "en")]
        public string En { get; set; }

        [DataMember(Name = "en_jp")]
        public string EnJp { get; set; }

        [DataMember(Name = "ja_jp")]
        public string JaJp { get; set; }
    }

    // One image's set of pre-generated sizes - every field optional/
    // nullable, matching every other catalogue-item model in this
    // project (CinemetaCatalogItem/AnimeFlvCatalogItem). Kitsu's own
    // response also includes a "meta.dimensions" object alongside these
    // URLs - deliberately not mapped here (an extra JSON field not
    // declared as a [DataMember] is silently ignored by
    // DataContractJsonSerializer, not a parse error).
    [DataContract]
    public class KitsuImageSet
    {
        [DataMember(Name = "tiny")]
        public string Tiny { get; set; }

        [DataMember(Name = "small")]
        public string Small { get; set; }

        [DataMember(Name = "medium")]
        public string Medium { get; set; }

        [DataMember(Name = "large")]
        public string Large { get; set; }

        [DataMember(Name = "original")]
        public string Original { get; set; }
    }

    // =============================================================
    // KITSU EPISODE RESOURCE
    // =============================================================

    [DataContract]
    public class KitsuEpisodeResource
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "attributes")]
        public KitsuEpisodeAttributes Attributes { get; set; }
    }

    [DataContract]
    public class KitsuEpisodeAttributes
    {
        // Absolute episode number for the series - this, NOT
        // seasonNumber, is what Kitsu/Torrentio/Comet's own
        // "kitsu:{id}:{episode}" id convention uses (no season
        // component at all - see this class's own remarks and
        // ContentRequest/PlaySelectedMediaAsync).
        [DataMember(Name = "number")]
        public int? Number { get; set; }

        // Present on many, not necessarily all, anime - used only for
        // EpisodeBrowserView's display grouping (defaults to season 1
        // when absent, a display convenience, never sent as part of a
        // Kitsu/Stremio id). Not renamed/reinterpreted from what Kitsu
        // itself calls it.
        [DataMember(Name = "seasonNumber")]
        public int? SeasonNumber { get; set; }

        [DataMember(Name = "canonicalTitle")]
        public string CanonicalTitle { get; set; }

        [DataMember(Name = "synopsis")]
        public string Synopsis { get; set; }

        [DataMember(Name = "airDate")]
        public string AirDate { get; set; }

        [DataMember(Name = "thumbnail")]
        public KitsuImageSet Thumbnail { get; set; }

        [DataMember(Name = "length")]
        public int? Length { get; set; }
    }

    // =============================================================
    // KITSU CATEGORY RESOURCE (Slice 17 addition)
    // =============================================================
    //
    // Field set live-confirmed against a real GET /anime?filter
    // [categories]=action&include=categories response's "included"
    // array this slice - id/title/slug/description/childCount/
    // totalMediaCount/nsfw/createdAt/updatedAt are all real fields;
    // only the ones BrowseAnimeAsync actually needs (title, slug) are
    // mapped here, matching this project's existing convention of not
    // mapping every field a response happens to contain (see
    // KitsuImageSet's own remarks on its ignored "meta.dimensions").

    [DataContract]
    public class KitsuCategoryResource
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "attributes")]
        public KitsuCategoryAttributes Attributes { get; set; }
    }

    [DataContract]
    public class KitsuCategoryAttributes
    {
        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }
    }

    // =============================================================
    // KITSU ANIME COLLECTION RESPONSE (Slice 17 addition - used only
    // by BrowseAnimeAsync)
    // =============================================================
    //
    // A separate response shape from KitsuCollectionResponse (used by
    // SearchAnimeAsync/GetPopularAnimeAsync, left unchanged) because
    // this one additionally carries the sideloaded "included" category
    // resources (only present when the request used include=
    // categories, which BrowseAnimeAsync always does) and the
    // top-level "meta.count" total, neither of which the existing
    // callers need.

    [DataContract]
    public class KitsuAnimeCollectionResponse
    {
        [DataMember(Name = "data")]
        public List<KitsuAnimeResource> Data { get; set; }

        [DataMember(Name = "included")]
        public List<KitsuCategoryResource> Included { get; set; }

        [DataMember(Name = "meta")]
        public KitsuCollectionMeta Meta { get; set; }
    }

    [DataContract]
    public class KitsuCollectionMeta
    {
        [DataMember(Name = "count")]
        public int Count { get; set; }
    }

    // Return shape for BrowseAnimeAsync - TotalCount is Kitsu's real
    // meta.count (the full matching-result count across every page,
    // not just this page's Items.Count), so the Anime Browse page can
    // know whether further pages exist without guessing from a full
    // page's worth of results.
    public class KitsuBrowseResult
    {
        public List<KitsuAnimeResource> Items { get; set; }
        public int TotalCount { get; set; }
    }
}
