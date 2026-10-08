using Newtonsoft.Json.Linq;
using StremioVideoCenter.Models;

namespace StremioVideoCenter.Services
{
    // Parses one mpv "track-list" entry. Only fields mpv actually reports are
    // populated; nothing is guessed.
    public static class MpvTrackParser
    {
        public static MpvTrackInfo Parse(JObject json)
        {
            if (json == null)
                return null;

            return new MpvTrackInfo
            {
                Id = json.Value<int?>("id") ?? 0,
                Type = json.Value<string>("type"),
                Language = json.Value<string>("lang"),
                Title = json.Value<string>("title"),
                Codec = json.Value<string>("codec"),
                ChannelCount = json.Value<int?>("demux-channel-count") ?? 0,
                IsSelected = json.Value<bool?>("selected") ?? false,
                IsDefault = json.Value<bool?>("default") ?? false,
                IsForced = json.Value<bool?>("forced") ?? false,
                IsExternal = json.Value<bool?>("external") ?? false
            };
        }
    }
}
