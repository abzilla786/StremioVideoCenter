using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Services
{
    // One provider (a single configured stream-provider addon instance) that
    // supplied at least one source in the current Source Selector list.
    public sealed class SourceProviderGroup
    {
        // Stable grouping key (see SourceProviderGrouping.GetKey). For a real
        // addon this is derived from its configured manifest URL, which can
        // carry the user's Real-Debrid key - so it is an INTERNAL identity
        // only: never displayed, logged or persisted. Empty string means
        // "sources that carry no addon identity".
        public string Key { get; set; }

        // Friendly display name (AddonInstance.FriendlyName). Never a URL.
        public string Label { get; set; }

        public int Count { get; set; }
    }

    // =============================================================
    // SOURCE PROVIDER GROUPING (Slice 19B)
    // =============================================================
    //
    // Pure, UI-free helpers behind the Source Selector's provider filter.
    // They operate only on the already-aggregated NormalizedPlaybackSource
    // list - no discovery, no network, no ranking.
    //
    // Identity: NormalizedPlaybackSource.SourceAddonUrl is set for every
    // aggregated source from the originating StremioAddonRegistry entry,
    // which de-duplicates entries by URL - so it identifies exactly one
    // configured addon instance, whatever the provider (Torrentio, Comet or
    // any other stream-provider addon). The friendly name is used only for
    // display, never for matching.
    public static class SourceProviderGrouping
    {
        public const string UnknownProviderLabel = "Unknown addon";

        public static string GetKey(NormalizedPlaybackSource source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.SourceAddonUrl))
            {
                return string.Empty;
            }

            return source.SourceAddonUrl.Trim();
        }

        // One group per distinct provider key, in order of first appearance
        // in the supplied (aggregated) sequence, so chip order is stable.
        // Two instances that share a friendly name get a numeric suffix so
        // their chips remain distinguishable.
        public static List<SourceProviderGroup> BuildGroups(IEnumerable<NormalizedPlaybackSource> sources)
        {
            var groups = new List<SourceProviderGroup>();

            if (sources == null)
            {
                return groups;
            }

            foreach (NormalizedPlaybackSource source in sources)
            {
                if (source == null)
                {
                    continue;
                }

                string key = GetKey(source);

                SourceProviderGroup group = groups.FirstOrDefault(
                    g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

                if (group == null)
                {
                    group = new SourceProviderGroup
                    {
                        Key = key,
                        Label = !string.IsNullOrWhiteSpace(source.SourceAddonName)
                            ? source.SourceAddonName.Trim()
                            : UnknownProviderLabel,
                        Count = 0
                    };

                    groups.Add(group);
                }

                group.Count++;
            }

            DisambiguateDuplicateLabels(groups);

            return groups;
        }

        // key == null means "All Sources": the list is returned as-is. The
        // relative order of the supplied list is always preserved.
        public static List<NormalizedPlaybackSource> Filter(
            IEnumerable<NormalizedPlaybackSource> sources,
            string key)
        {
            if (sources == null)
            {
                return new List<NormalizedPlaybackSource>();
            }

            if (key == null)
            {
                return sources.Where(source => source != null).ToList();
            }

            return sources
                .Where(source => source != null &&
                       string.Equals(GetKey(source), key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Returns the group key to restore, or null (All Sources) when no
        // preference was given or it no longer exists in the new list.
        public static string ResolveRestoredKey(
            IList<SourceProviderGroup> groups,
            string preferredKey)
        {
            if (preferredKey == null || groups == null)
            {
                return null;
            }

            SourceProviderGroup match = groups.FirstOrDefault(
                g => string.Equals(g.Key, preferredKey, StringComparison.OrdinalIgnoreCase));

            return match != null ? match.Key : null;
        }

        private static void DisambiguateDuplicateLabels(List<SourceProviderGroup> groups)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (SourceProviderGroup group in groups)
            {
                int occurrence;

                if (seen.TryGetValue(group.Label, out occurrence))
                {
                    occurrence++;
                    seen[group.Label] = occurrence;
                    group.Label = group.Label + " (" + occurrence + ")";
                }
                else
                {
                    seen[group.Label] = 1;
                }
            }
        }
    }
}
