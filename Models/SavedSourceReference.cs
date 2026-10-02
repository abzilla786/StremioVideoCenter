using System;

namespace StremioVideoCenter.Models
{
    // A stable, NON-SECRET description of the source that last played
    // successfully for one profile + media identity. Persisted on
    // WatchStateEntry so Continue Watching / History can later resume the
    // same source without showing the source selector.
    //
    // Deliberately holds no URL of any kind: stream URLs expire and
    // addon manifest URLs can embed credentials (e.g. a Real-Debrid key).
    // Instead it keeps identifiers that survive re-resolution:
    //   - AddonName           : which addon produced it (display-safe name)
    //   - InfoHash + FileIdx  : torrent identity, when the stream has one
    //   - Filename + FileSizeBytes : release identity from behaviorHints
    //   - Name                : the addon's stream name (e.g. "RD 1080p")
    //   - Kind                : direct vs Real-Debrid-resolved
    // A future resume flow re-aggregates sources for the media and uses
    // Matches() to find the saved one among the fresh, valid URLs.
    public class SavedSourceReference
    {
        public string AddonName { get; set; }

        public string InfoHash { get; set; }

        public int? FileIdx { get; set; }

        public string Filename { get; set; }

        public long? FileSizeBytes { get; set; }

        public string Name { get; set; }

        public PlaybackSourceKind Kind { get; set; }

        public DateTime SavedUtc { get; set; }

        // True when there is at least one identifier strong enough to
        // find this source again later.
        public bool IsUsable
        {
            get
            {
                return !string.IsNullOrWhiteSpace(InfoHash) ||
                       !string.IsNullOrWhiteSpace(Filename) ||
                       !string.IsNullOrWhiteSpace(Name);
            }
        }

        public static SavedSourceReference FromSource(NormalizedPlaybackSource source)
        {
            if (source == null)
                return null;

            StremioStream stream = source.Source != null ? source.Source.OriginatingStream : null;

            SavedSourceReference reference = new SavedSourceReference
            {
                AddonName = Clean(source.SourceAddonName),
                Name = Clean(source.Name),
                Kind = source.Kind,
                Filename = Clean(source.ReleaseFilename),
                FileSizeBytes = source.FileSizeBytes,
                SavedUtc = DateTime.UtcNow
            };

            if (stream != null)
            {
                reference.InfoHash = Clean(stream.InfoHash);
                reference.FileIdx = stream.FileIdx;
            }

            return reference.IsUsable ? reference : null;
        }

        // Strongest identifier wins: torrent identity (hash + file index),
        // then release filename (+ size when both sides know it), then the
        // addon's stream name. Never compares URLs.
        public bool Matches(NormalizedPlaybackSource candidate)
        {
            if (candidate == null || !IsUsable)
                return false;

            StremioStream stream = candidate.Source != null ? candidate.Source.OriginatingStream : null;

            if (!string.IsNullOrEmpty(InfoHash) && stream != null && !string.IsNullOrEmpty(stream.InfoHash))
            {
                if (!string.Equals(InfoHash, stream.InfoHash.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;

                return !FileIdx.HasValue || !stream.FileIdx.HasValue || FileIdx.Value == stream.FileIdx.Value;
            }

            // Below the torrent tier the addon must agree (when both sides
            // know it): the same filename/name from a different addon is not
            // necessarily the same stream.
            if (!string.IsNullOrEmpty(AddonName) && !string.IsNullOrWhiteSpace(candidate.SourceAddonName) &&
                !string.Equals(AddonName, candidate.SourceAddonName.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(Filename) && !string.IsNullOrWhiteSpace(candidate.ReleaseFilename))
            {
                if (!string.Equals(Filename, candidate.ReleaseFilename.Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;

                return !FileSizeBytes.HasValue || !candidate.FileSizeBytes.HasValue ||
                       FileSizeBytes.Value == candidate.FileSizeBytes.Value;
            }

            return !string.IsNullOrEmpty(Name) &&
                   string.Equals(Name, Clean(candidate.Name), StringComparison.OrdinalIgnoreCase);
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
