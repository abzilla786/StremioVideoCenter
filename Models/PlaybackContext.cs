using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    // Everything the player needs to know about ONE playback attempt,
    // captured once when the attempt starts and passed down unchanged:
    //   StremioVideoCenterView -> MpvPlayer -> PlayerSession -> mpv
    //
    // It only wraps existing models (MediaItem, NormalizedPlaybackSource);
    // it does not introduce a second identity scheme. CanonicalKey is
    // exactly ContentRequest.BuildStremioId() ("tt…", "tt…:S:E",
    // "kitsu:id:episode").
    //
    // ProfileId is captured at start so a profile switch (or any later
    // GetActiveProfileId() call) can never redirect this attempt's
    // progress into another profile's Watch State.
    //
    // Immutable: a recovery retry with a different source creates a NEW
    // context via WithSource, leaving the original untouched.
    //
    // The context holds the stream URL (inside Source) because the player
    // must be able to load it. It must never be logged or displayed -
    // ToString() is deliberately URL-free.
    public sealed class PlaybackContext
    {
        public Guid ProfileId { get; private set; }

        public string CanonicalKey { get; private set; }

        public ContentType ContentType { get; private set; }

        public MediaItem MediaItem { get; private set; }

        public NormalizedPlaybackSource Source { get; private set; }

        // Position to resume at once mpv confirms the file is loaded, or
        // null to start from the beginning. Null/<=0 both mean "no resume".
        public double? StartPositionSeconds { get; private set; }

        // The other sources offered for this media (session-scoped, in
        // memory only). Failure recovery removes the failed candidate from
        // this list and re-shows the Source Selector with what remains.
        // Shared by reference between a context and its WithSource copies
        // so the removals accumulate across retries. May be empty.
        public List<NormalizedPlaybackSource> Candidates { get; private set; }

        public PlaybackContext(
            Guid profileId,
            string canonicalKey,
            ContentType contentType,
            MediaItem mediaItem,
            NormalizedPlaybackSource source,
            double? startPositionSeconds,
            IEnumerable<NormalizedPlaybackSource> candidates = null)
        {
            if (source == null || source.Source == null)
                throw new ArgumentNullException(nameof(source));

            ProfileId = profileId;
            CanonicalKey = canonicalKey;
            ContentType = contentType;
            MediaItem = mediaItem;
            Source = source;
            StartPositionSeconds = startPositionSeconds;
            Candidates = candidates != null
                ? new List<NormalizedPlaybackSource>(candidates)
                : new List<NormalizedPlaybackSource>();
        }

        // True when Watch State/History can be recorded for this attempt.
        public bool HasIdentity
        {
            get { return ProfileId != Guid.Empty && !string.IsNullOrWhiteSpace(CanonicalKey); }
        }

        // The playable URL. Internal to the playback path - never log it.
        public string PlaybackUrl
        {
            get { return Source.Source.Url; }
        }

        public bool HasResumePosition
        {
            get { return StartPositionSeconds.HasValue && StartPositionSeconds.Value > 0; }
        }

        // Same media/profile/resume position, different source (used by
        // failure recovery when falling back to the next candidate).
        public PlaybackContext WithSource(NormalizedPlaybackSource source)
        {
            return new PlaybackContext(
                ProfileId, CanonicalKey, ContentType, MediaItem, source, StartPositionSeconds, Candidates);
        }

        // Stable, non-secret description of the source for the Watch State.
        public SavedSourceReference BuildSavedSource()
        {
            return SavedSourceReference.FromSource(Source);
        }

        public override string ToString()
        {
            return "PlaybackContext(" + CanonicalKey + ")";
        }
    }
}
