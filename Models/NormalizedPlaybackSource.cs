using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // A consistent, addon-independent representation of one playable
    // source, intended for a future source-selection UI. Separate from
    // PlaybackSource (the lean player-facing contract) rather than another
    // extension to it - this carries display metadata the player itself
    // will never need, and PlaybackSource is deliberately kept free of
    // that concern.
    //
    // Holds a reference back to the PlaybackSource it was built from
    // (Source) rather than being a disconnected DTO - a future selection
    // UI's "play this one" action is simply
    // MpvPlayer.PlayVideoAsync(normalized.Source.Url), unchanged from how
    // the player is called today.
    //
    // Produced by PlaybackSourceNormalizationService. Never ranked, scored,
    // or ordered by "best" here or anywhere else this slice.
    public class NormalizedPlaybackSource
    {
        // The underlying playable source. Never null for a normalized
        // source produced by PlaybackSourceNormalizationService.
        public PlaybackSource Source { get; set; }

        // =========================================================
        // SOURCE IDENTITY - always available, copied straight from Source
        // =========================================================

        public string Name { get; set; }

        public string Title { get; set; }

        // Official Stremio protocol field, the eventual replacement for
        // Title (see StremioStream.Description). Kept fully separate -
        // never overwrites, never falls back to, Title.
        public string Description { get; set; }

        public string Url { get; set; }

        // Manifest URL of the originating addon - carries the user's
        // Real-Debrid key for an RD-configured addon. Kept here because
        // Source is the disconnected-DTO-avoidance escape hatch this type
        // documents itself as not being, but nothing should display this
        // directly - see SourceAddonName.
        public string SourceAddonUrl { get; set; }

        // Display-safe addon identity (AddonInstance.FriendlyName, falling
        // back to AddonId) - what a source-selection UI or diagnostic
        // output should show instead of SourceAddonUrl.
        public string SourceAddonName { get; set; }

        public PlaybackSourceKind Kind { get; set; }

        // =========================================================
        // CONSERVATIVE PARSING - see PlaybackSourceNormalizationService
        // =========================================================

        // e.g. 1080, 2160 - null when not confidently identified from an
        // explicit, known token in Name/Title. Never guessed.
        public int? ResolutionHeight { get; set; }

        // =========================================================
        // STRUCTURED METADATA - populated only when the addon's own
        // behaviorHints/subtitles actually provided it, never guessed
        // =========================================================

        public long? FileSizeBytes { get; set; }

        public string ReleaseFilename { get; set; }

        // NOT an official Stremio field - there is no separate structured
        // source for "release name" distinct from the filename. This is a
        // deterministically derived convenience value: ReleaseFilename with
        // its file extension removed (e.g.
        // "Big.Buck.Bunny.2008.1080p.mkv" -> "Big.Buck.Bunny.2008.1080p").
        // Null whenever ReleaseFilename is null. Purely a formatting
        // transform, not parsing or guessing.
        public string ReleaseName { get; set; }

        public List<string> SubtitleLanguages { get; set; }

        // =========================================================
        // CONSERVATIVE TEXT-TOKEN PARSING - see
        // PlaybackSourceNormalizationService. Null whenever there isn't a
        // confident, explicit token match - never guessed, never inferred
        // from another field (e.g. HDR is never assumed from a 2160p
        // resolution alone).
        // =========================================================

        // e.g. "H264", "H265", "AV1", "VP9".
        public string CodecInfo { get; set; }

        // e.g. "HDR10", "HDR10+", "Dolby Vision".
        public string HdrInfo { get; set; }

        // "Atmos" or null - kept as its own field rather than folded into
        // AudioInfo, since a stream can be e.g. both TrueHD and Atmos at
        // once and neither implies the other.
        public string AtmosInfo { get; set; }

        // Audio codec specifically (e.g. "DTS-HD", "TrueHD", "AC3", "AAC") -
        // not a general-purpose audio description field.
        public string AudioInfo { get; set; }

        // Deliberately deferred - always null this slice. Numeric channel
        // tokens like "5.1"/"7.1" have a real false-positive collision risk
        // that the other tokens above don't (e.g. a date-stamped release
        // like "2024.07.01" contains "7.1" as a contiguous substring).
        // Scaffolded now so a later slice adding real support for it
        // doesn't need another shape change.
        public string AudioChannels { get; set; }

        // Derived from ReleaseFilename's extension only (e.g. ".mkv" ->
        // "MKV") - a direct, deterministic mapping, not text inference.
        public string ContainerInfo { get; set; }
    }
}
