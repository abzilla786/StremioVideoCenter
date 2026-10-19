using StremioVideoCenter.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace StremioVideoCenter.Models
{
    // =============================================================
    // SUBTITLE OPTIONS (Slice 20 Stage 3)
    // =============================================================
    //
    // What the full-page subtitle selector shows: every subtitle the user
    // can choose, grouped by language. Two kinds, kept explicit:
    //
    //   Embedded  - a subtitle track mpv already reports for the loaded
    //               file (MpvTrackInfo). Activated by track id.
    //   External  - a subtitle an addon offered (AddonSubtitle). Activated
    //               by handing its URL to mpv; once mpv has added it, it
    //               shows up in mpv's own track list and is represented
    //               by that track from then on (so it is never listed twice).
    //
    // Pure data and rules (no mpv, no WPF, no network). The external URL
    // is INTERNAL: it exists only so the loading path can pass it to mpv.
    // It is never displayed and never logged.

    public enum SubtitleOptionKind
    {
        Embedded,
        External
    }

    public sealed class SubtitleOption
    {
        public SubtitleOptionKind Kind { get; set; }

        public string LanguageKey { get; set; }

        public string LanguageDisplay { get; set; }

        // User-facing line, e.g. "English - SDH" or "OpenSubtitles - Option 2".
        public string Label { get; set; }

        // User-facing origin tag: "Embedded", "External" or the offering
        // addon's display name.
        public string SourceLabel { get; set; }

        // The mpv subtitle track id. Set for every embedded option and for
        // an external subtitle that mpv has already loaded; null for an
        // external subtitle that has not been added yet.
        public int? TrackId { get; set; }

        public bool IsSelected { get; set; }

        // Stable identity for focus restoration and dedup.
        public string Identity { get; set; }

        // The language code exactly as the provider sent it (passed to mpv
        // as the new track's language tag).
        public string RawLanguageCode { get; set; }

        // Internal-only. Never shown, never logged.
        internal string ExternalKey { get; set; }

        internal string ExternalUrl { get; set; }

        public bool NeedsLoading
        {
            get { return Kind == SubtitleOptionKind.External && !TrackId.HasValue; }
        }
    }

    public sealed class SubtitleLanguageGroup
    {
        public SubtitleLanguageGroup(string key, string display)
        {
            Key = key;
            Display = display;
            Options = new List<SubtitleOption>();
        }

        public string Key { get; private set; }

        public string Display { get; private set; }

        public List<SubtitleOption> Options { get; private set; }

        public bool HasSelected
        {
            get { return Options.Any(o => o.IsSelected); }
        }

        public int EmbeddedCount
        {
            get { return Options.Count(o => o.Kind == SubtitleOptionKind.Embedded); }
        }
    }

    // =============================================================
    // LANGUAGE NORMALISATION
    // =============================================================
    //
    // Providers disagree on language codes: mpv gives whatever a file
    // carries (usually ISO 639-2, sometimes 639-1 or a full name),
    // OpenSubtitles v3 gives three-letter codes including some that are
    // not ISO ("pob", "zht"...). Both are mapped to ONE display name, and
    // the lower-cased display name is the grouping key - so "en", "eng"
    // and "English" land in the same group. A code nobody knows is shown
    // as given (upper-cased when it is a short code), never dropped.
    public static class SubtitleLanguages
    {
        public const string UnknownKey = "unknown";
        public const string UnknownDisplay = "Unknown language";

        // Codes TrackLabels does not know, plus the non-ISO codes some
        // providers use. Display names follow TrackLabels' conventions
        // ("Portuguese (BR)") so an embedded "pt-BR" and an external "pob"
        // are the same group.
        private static readonly Dictionary<string, string> Extra =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"pob","Portuguese (BR)"},{"pom","Portuguese (MZ)"},
            {"zhc","Chinese (Simplified)"},{"zht","Chinese (Traditional)"},{"ze","Chinese (Bilingual)"},
            {"hr","Croatian"},{"hrv","Croatian"},{"sr","Serbian"},{"srp","Serbian"},
            {"sk","Slovak"},{"slk","Slovak"},{"slo","Slovak"},{"sl","Slovenian"},{"slv","Slovenian"},
            {"lt","Lithuanian"},{"lit","Lithuanian"},{"lv","Latvian"},{"lav","Latvian"},
            {"et","Estonian"},{"est","Estonian"},{"ca","Catalan"},{"cat","Catalan"},
            {"ms","Malay"},{"msa","Malay"},{"may","Malay"},{"bn","Bengali"},{"ben","Bengali"},
            {"ta","Tamil"},{"tam","Tamil"},{"te","Telugu"},{"tel","Telugu"},{"ml","Malayalam"},{"mal","Malayalam"},
            {"mk","Macedonian"},{"mkd","Macedonian"},{"mac","Macedonian"},
            {"sq","Albanian"},{"sqi","Albanian"},{"alb","Albanian"},{"bs","Bosnian"},{"bos","Bosnian"},
            {"is","Icelandic"},{"isl","Icelandic"},{"ice","Icelandic"},{"ka","Georgian"},{"kat","Georgian"},{"geo","Georgian"},
            {"hy","Armenian"},{"hye","Armenian"},{"arm","Armenian"},{"af","Afrikaans"},{"afr","Afrikaans"},
            {"eu","Basque"},{"eus","Basque"},{"baq","Basque"},{"gl","Galician"},{"glg","Galician"},
            {"tl","Filipino"},{"tgl","Filipino"},{"fil","Filipino"},{"si","Sinhala"},{"sin","Sinhala"},
            {"ne","Nepali"},{"nep","Nepali"},{"my","Burmese"},{"mya","Burmese"},{"bur","Burmese"},
            {"km","Khmer"},{"khm","Khmer"},{"kk","Kazakh"},{"kaz","Kazakh"},{"az","Azerbaijani"},{"aze","Azerbaijani"},
            {"be","Belarusian"},{"bel","Belarusian"},{"mr","Marathi"},{"mar","Marathi"},{"pa","Punjabi"},{"pan","Punjabi"},
            {"ku","Kurdish"},{"kur","Kurdish"},{"mn","Mongolian"},{"mon","Mongolian"},{"lb","Luxembourgish"},{"ltz","Luxembourgish"}
        };

        private static readonly HashSet<string> Undetermined =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "und", "unk", "mis", "zxx", "mul", "unknown" };

        public static void Normalize(string code, out string key, out string display)
        {
            if (string.IsNullOrWhiteSpace(code) || Undetermined.Contains(code.Trim()))
            {
                key = UnknownKey;
                display = UnknownDisplay;
                return;
            }

            string trimmed = code.Trim();
            string name;

            // The non-ISO / extra codes win over TrackLabels so "pob" and
            // friends resolve before the generic lookup returns them raw.
            string[] parts = trimmed.Split(new[] { '-', '_' }, 2);

            if (Extra.TryGetValue(trimmed, out name))
            {
                display = name;
            }
            else
            {
                string known = TrackLabels.LanguageName(trimmed);

                if (!string.Equals(known, trimmed, StringComparison.Ordinal))
                {
                    display = known;
                }
                else if (Extra.TryGetValue(parts[0], out name))
                {
                    display = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                        ? name + " (" + parts[1].ToUpperInvariant() + ")"
                        : name;
                }
                else
                {
                    // Unknown: shown as the provider gave it. Short codes
                    // are upper-cased to read as codes; anything longer is
                    // probably already a name.
                    display = trimmed.Length <= 3 ? trimmed.ToUpperInvariant() : trimmed;
                }
            }

            key = display.ToLowerInvariant();
        }
    }

    // =============================================================
    // CATALOG BUILDER
    // =============================================================
    public static class SubtitleCatalog
    {
        // Stable, non-reversible key for an external subtitle, derived from
        // its URL. Used to recognise "the same subtitle" without ever
        // keeping the URL anywhere user-visible or loggable.
        internal static string ExternalKeyFor(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                StringBuilder builder = new StringBuilder(16);

                for (int i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        // embeddedTracks: mpv's current subtitle track list (it also holds
        //   every external subtitle mpv has loaded).
        // external: the addons' results (may be null while loading).
        // loadedExternal: external key -> mpv track id, for subtitles added
        //   during THIS playback (PlayerSession keeps it).
        internal static List<SubtitleLanguageGroup> Build(
            IReadOnlyList<MpvTrackInfo> embeddedTracks,
            int? selectedTrackId,
            IEnumerable<AddonSubtitle> external,
            IDictionary<string, int> loadedExternal)
        {
            Dictionary<string, SubtitleLanguageGroup> groups =
                new Dictionary<string, SubtitleLanguageGroup>(StringComparer.Ordinal);

            IReadOnlyList<MpvTrackInfo> tracks = embeddedTracks ?? new List<MpvTrackInfo>();
            IReadOnlyList<string> labels = TrackLabels.DescribeAll(tracks);

            // track id -> external key, for tracks that still exist.
            Dictionary<int, string> loadedById = new Dictionary<int, string>();
            HashSet<string> loadedKeysPresent = new HashSet<string>(StringComparer.Ordinal);

            if (loadedExternal != null)
            {
                foreach (KeyValuePair<string, int> pair in loadedExternal)
                {
                    if (tracks.Any(t => t.Id == pair.Value) && !loadedById.ContainsKey(pair.Value))
                    {
                        loadedById[pair.Value] = pair.Key;
                        loadedKeysPresent.Add(pair.Key);
                    }
                }
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                MpvTrackInfo track = tracks[i];
                string key;
                string display;
                SubtitleLanguages.Normalize(track.Language, out key, out display);

                string externalKey;
                bool loadedFromAddon = loadedById.TryGetValue(track.Id, out externalKey);

                SubtitleLanguageGroup group = GetGroup(groups, key, display);

                group.Options.Add(new SubtitleOption
                {
                    Kind = loadedFromAddon ? SubtitleOptionKind.External : SubtitleOptionKind.Embedded,
                    LanguageKey = key,
                    LanguageDisplay = display,
                    Label = labels[i],
                    SourceLabel = loadedFromAddon || track.IsExternal ? "External" : "Embedded",
                    TrackId = track.Id,
                    IsSelected = selectedTrackId.HasValue && selectedTrackId.Value == track.Id,
                    Identity = "t:" + track.Id,
                    RawLanguageCode = track.Language,
                    ExternalKey = loadedFromAddon ? externalKey : null
                });
            }

            if (external != null)
            {
                HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> numbering = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (AddonSubtitle subtitle in external)
                {
                    if (subtitle == null)
                    {
                        continue;
                    }

                    string externalKey = ExternalKeyFor(subtitle.Url);

                    if (externalKey == null || !seenKeys.Add(externalKey))
                    {
                        continue;
                    }

                    // The same addon offering the same id twice is one
                    // subtitle. Different ids/URLs are different releases
                    // and are never merged.
                    if (!string.IsNullOrWhiteSpace(subtitle.Id) &&
                        !seenIds.Add((subtitle.SourceAddonName ?? string.Empty) + "|" + subtitle.Id))
                    {
                        continue;
                    }

                    // Already added to mpv: it is listed above as its track.
                    if (loadedKeysPresent.Contains(externalKey))
                    {
                        continue;
                    }

                    string key;
                    string display;
                    SubtitleLanguages.Normalize(subtitle.Language, out key, out display);

                    string source = string.IsNullOrWhiteSpace(subtitle.SourceAddonName)
                        ? "Addon"
                        : subtitle.SourceAddonName.Trim();

                    string numberKey = key + "|" + source;
                    int number;
                    numbering.TryGetValue(numberKey, out number);
                    number++;
                    numbering[numberKey] = number;

                    GetGroup(groups, key, display).Options.Add(new SubtitleOption
                    {
                        Kind = SubtitleOptionKind.External,
                        LanguageKey = key,
                        LanguageDisplay = display,
                        Label = string.IsNullOrWhiteSpace(subtitle.Label)
                            ? source + " · Option " + number
                            : source + " · " + subtitle.Label,
                        SourceLabel = source,
                        TrackId = null,
                        IsSelected = false,
                        Identity = "x:" + externalKey,
                        RawLanguageCode = subtitle.Language,
                        ExternalKey = externalKey,
                        ExternalUrl = subtitle.Url
                    });
                }
            }

            // Deterministic order: English first, unknown last, the rest
            // alphabetical. Within a group: tracks mpv already has (in
            // mpv's order), then addon results in the order they arrived.
            return groups.Values
                .OrderBy(g => g.Key == "english" ? 0 : (g.Key == SubtitleLanguages.UnknownKey ? 2 : 1))
                .ThenBy(g => g.Display, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static SubtitleLanguageGroup GetGroup(
            Dictionary<string, SubtitleLanguageGroup> groups,
            string key,
            string display)
        {
            SubtitleLanguageGroup group;

            if (!groups.TryGetValue(key, out group))
            {
                group = new SubtitleLanguageGroup(key, display);
                groups[key] = group;
            }

            return group;
        }
    }
}
