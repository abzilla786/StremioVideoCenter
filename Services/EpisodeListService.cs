using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // Loads the ordered episode list the player needs for Previous/Next and
    // auto-next, from the SAME providers the Episode Browser uses (Cinemeta
    // for "tt…" shows, Kitsu for "kitsu:<id>" anime). Fail-soft: a provider
    // failure yields null (no navigation), never an exception.
    //
    // Lists are cached per show for the session, so a transition after the
    // first load is instant and does not hit the network.
    //
    // Episodes that have not aired yet (a parseable air date in the future)
    // are left out of the navigation list: offering one as "Next" would just
    // end in "no playable sources".
    public sealed class EpisodeListService
    {
        private readonly CinemetaService _cinemeta;
        private readonly KitsuService _kitsu;
        private readonly Dictionary<string, EpisodeList> _cache =
            new Dictionary<string, EpisodeList>(StringComparer.OrdinalIgnoreCase);

        public EpisodeListService(CinemetaService cinemeta, KitsuService kitsu)
        {
            _cinemeta = cinemeta;
            _kitsu = kitsu;
        }

        public EpisodeList TryGetCached(string showStremioId)
        {
            EpisodeList list;
            return !string.IsNullOrWhiteSpace(showStremioId) && _cache.TryGetValue(showStremioId, out list)
                ? list
                : null;
        }

        public async Task<EpisodeList> GetAsync(string showStremioId)
        {
            if (string.IsNullOrWhiteSpace(showStremioId))
            {
                return null;
            }

            EpisodeList cached = TryGetCached(showStremioId);
            if (cached != null)
            {
                return cached;
            }

            EpisodeList list;

            try
            {
                list = showStremioId.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase)
                    ? await LoadKitsuAsync(showStremioId)
                    : await LoadCinemetaAsync(showStremioId);
            }
            catch (Exception ex)
            {
                // Recovery unchanged (no list, so no Previous/Next), but
                // the failure is now logged. Only the show id is logged.
                Playnite.SDK.LogManager.GetLogger().Warn(
                    ex,
                    "EpisodeListService: failed to load the episode list for " + showStremioId + ".");
                list = null;
            }

            if (list != null && list.Count > 0)
            {
                _cache[showStremioId] = list;
                return list;
            }

            return null;
        }

        private async Task<EpisodeList> LoadCinemetaAsync(string showId)
        {
            if (_cinemeta == null || !showId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            CinemetaMeta meta = await _cinemeta.GetMetaAsync("series", showId);

            if (meta == null || meta.Videos == null)
            {
                return null;
            }

            DateTime now = DateTime.UtcNow;

            return EpisodeList.Create(
                showId,
                meta.Videos
                    .Where(v => v != null && v.Number > 0 && !EpisodeList.IsFutureAirDate(v.FirstAired ?? v.Released, now))
                    .Select(v => new EpisodeRef
                    {
                        // Exactly ContentRequest.BuildStremioId()'s shape.
                        CanonicalKey = showId + ":" + v.Season + ":" + v.Number,
                        ShowStremioId = showId,
                        Season = v.Season,
                        Episode = v.Number,
                        Title = v.Name,
                        IsKitsu = false
                    }));
        }

        private async Task<EpisodeList> LoadKitsuAsync(string showId)
        {
            if (_kitsu == null)
            {
                return null;
            }

            string kitsuId = showId.Substring("kitsu:".Length);
            List<KitsuEpisodeResource> episodes = await _kitsu.GetEpisodesAsync(kitsuId);

            if (episodes == null)
            {
                return null;
            }

            DateTime now = DateTime.UtcNow;

            return EpisodeList.Create(
                showId,
                episodes
                    .Where(e => e != null && e.Attributes != null && e.Attributes.Number.HasValue &&
                                !EpisodeList.IsFutureAirDate(e.Attributes.AirDate, now))
                    .Select(e => new EpisodeRef
                    {
                        // "kitsu:<id>:<absoluteEpisodeNumber>" - no season.
                        CanonicalKey = showId + ":" + e.Attributes.Number.Value,
                        ShowStremioId = showId,
                        Season = e.Attributes.SeasonNumber ?? 1,
                        Episode = e.Attributes.Number.Value,
                        Title = e.Attributes.CanonicalTitle,
                        IsKitsu = true
                    }));
        }
    }
}
