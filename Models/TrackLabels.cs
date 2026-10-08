using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Models
{
    // Turns mpv track metadata into a short menu label. Uses only what mpv
    // reports: language, title, codec, channel count and the default/forced/
    // external flags. A language code is shown as its standard English name
    // when it is a well-known ISO 639 code, otherwise exactly as mpv gave it.
    // Nothing else is invented; with no metadata at all the label is
    // "Track <id>".
    public static class TrackLabels
    {
        private static readonly Dictionary<string, string> Languages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"en","English"},{"eng","English"},{"ja","Japanese"},{"jpn","Japanese"},
            {"fr","French"},{"fra","French"},{"fre","French"},{"de","German"},{"deu","German"},{"ger","German"},
            {"es","Spanish"},{"spa","Spanish"},{"it","Italian"},{"ita","Italian"},
            {"pt","Portuguese"},{"por","Portuguese"},{"ru","Russian"},{"rus","Russian"},
            {"zh","Chinese"},{"zho","Chinese"},{"chi","Chinese"},{"ko","Korean"},{"kor","Korean"},
            {"ar","Arabic"},{"ara","Arabic"},{"hi","Hindi"},{"hin","Hindi"},{"nl","Dutch"},{"nld","Dutch"},{"dut","Dutch"},
            {"sv","Swedish"},{"swe","Swedish"},{"no","Norwegian"},{"nor","Norwegian"},{"da","Danish"},{"dan","Danish"},
            {"fi","Finnish"},{"fin","Finnish"},{"pl","Polish"},{"pol","Polish"},{"tr","Turkish"},{"tur","Turkish"},
            {"he","Hebrew"},{"heb","Hebrew"},{"th","Thai"},{"tha","Thai"},{"vi","Vietnamese"},{"vie","Vietnamese"},
            {"id","Indonesian"},{"ind","Indonesian"},{"cs","Czech"},{"ces","Czech"},{"cze","Czech"},
            {"el","Greek"},{"ell","Greek"},{"gre","Greek"},{"hu","Hungarian"},{"hun","Hungarian"},
            {"ro","Romanian"},{"ron","Romanian"},{"rum","Romanian"},{"uk","Ukrainian"},{"ukr","Ukrainian"},
            {"bg","Bulgarian"},{"bul","Bulgarian"},{"fa","Persian"},{"fas","Persian"},{"per","Persian"},
            {"ur","Urdu"},{"urd","Urdu"}
        };

        // "eng" -> "English"; "pt-BR" -> "Portuguese (BR)"; unknown codes are
        // returned as mpv reported them; null/blank -> null.
        public static string LanguageName(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            string trimmed = code.Trim();
            string[] parts = trimmed.Split(new[] { '-', '_' }, 2);
            string name;

            if (Languages.TryGetValue(parts[0], out name))
                return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                    ? name + " (" + parts[1].ToUpperInvariant() + ")"
                    : name;

            return trimmed;
        }

        public static string ChannelLayout(int channels)
        {
            switch (channels)
            {
                case 1: return "1.0";
                case 2: return "2.0";
                case 6: return "5.1";
                case 8: return "7.1";
                default: return channels > 0 ? channels + "ch" : null;
            }
        }

        // One track's label. includeDefaultTag is false for a single track
        // (where "default" says nothing).
        public static string Describe(MpvTrackInfo track, bool includeDefaultTag)
        {
            if (track == null)
                return string.Empty;

            string language = LanguageName(track.Language);
            string title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title.Trim();

            string label;
            if (language != null && title != null)
            {
                // Titles often already say the language ("English SDH").
                label = title.IndexOf(language, StringComparison.OrdinalIgnoreCase) >= 0
                    ? title
                    : language + " — " + title;
            }
            else
            {
                label = language ?? title ?? ("Track " + track.Id);
            }

            if (string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(track.Codec))
            {
                string layout = ChannelLayout(track.ChannelCount);
                label += " · " + track.Codec.Trim().ToUpperInvariant() + (layout != null ? " " + layout : string.Empty);
            }

            if (track.IsForced) label += " (forced)";
            if (track.IsExternal) label += " (external)";
            if (includeDefaultTag && track.IsDefault) label += " (default)";

            return label;
        }

        // Labels for a whole list, in order. Tracks that would otherwise read
        // identically (duplicate language labels) get their track id appended
        // so the user can tell them apart.
        public static IReadOnlyList<string> DescribeAll(IReadOnlyList<MpvTrackInfo> tracks)
        {
            if (tracks == null || tracks.Count == 0)
                return new List<string>();

            bool several = tracks.Count > 1;
            List<string> labels = tracks.Select(t => Describe(t, several)).ToList();

            HashSet<string> duplicated = new HashSet<string>(
                labels.GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                      .Where(g => g.Count() > 1)
                      .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < labels.Count; i++)
            {
                if (duplicated.Contains(labels[i]))
                    labels[i] += " · #" + tracks[i].Id;
            }

            return labels;
        }
    }
}
