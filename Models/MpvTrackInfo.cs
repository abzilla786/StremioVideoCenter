using Newtonsoft.Json.Linq;

namespace StremioVideoCenter.Models
{
    // One entry from mpv's "track-list" property. Populated via
    // MpvTrackInfo.FromJson, never constructed by hand from UI code.
    public sealed class MpvTrackInfo
    {
        public int Id { get; set; }

        // "audio" or "sub" (mpv's own type strings).
        public string Type { get; set; }

        public string Language { get; set; }
        public string Title { get; set; }
        public bool IsSelected { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        public bool IsExternal { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title))
                    return Title;

                if (!string.IsNullOrWhiteSpace(Language))
                    return Language;

                return "Track " + Id;
            }
        }

        public static MpvTrackInfo FromJson(JObject json)
        {
            if (json == null)
                return null;

            return new MpvTrackInfo
            {
                Id = json.Value<int?>("id") ?? 0,
                Type = json.Value<string>("type"),
                Language = json.Value<string>("lang"),
                Title = json.Value<string>("title"),
                IsSelected = json.Value<bool?>("selected") ?? false,
                IsDefault = json.Value<bool?>("default") ?? false,
                IsForced = json.Value<bool?>("forced") ?? false,
                IsExternal = json.Value<bool?>("external") ?? false
            };
        }
    }
}
