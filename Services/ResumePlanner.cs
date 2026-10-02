using StremioVideoCenter.Models;
using System;
using System.Collections.Generic;

namespace StremioVideoCenter.Services
{
    // Everything needed to resume one incomplete item, read from Watch State
    // for ONE profile. Used by both Continue Watching and History so there is
    // a single resume path.
    public sealed class ResumePlan
    {
        // The profile whose Watch State this plan came from. The resulting
        // PlaybackContext must use this profile, not the live active one.
        public Guid ProfileId { get; private set; }

        public WatchStateEntry Entry { get; private set; }

        public string CanonicalKey { get { return Entry.CanonicalKey; } }

        public ContentType ContentType { get { return Entry.ContentType; } }

        // Where to seek once mpv has established playback; null = start at
        // the beginning.
        public double? StartPositionSeconds { get; private set; }

        // The source that last established playback successfully, or null.
        public SavedSourceReference SavedSource { get { return Entry.SavedSource; } }

        public ResumePlan(Guid profileId, WatchStateEntry entry, double? startPositionSeconds)
        {
            ProfileId = profileId;
            Entry = entry;
            StartPositionSeconds = startPositionSeconds;
        }
    }

    public static class ResumePlanner
    {
        // Null when the item is not resumable: no profile, no entry, or a
        // completed entry (a completed item is never resumed from its old
        // position).
        public static ResumePlan Create(WatchStateService watchState, Guid profileId, string canonicalKey)
        {
            if (watchState == null || profileId == Guid.Empty || string.IsNullOrWhiteSpace(canonicalKey))
            {
                return null;
            }

            return Create(profileId, watchState.GetEntry(profileId, canonicalKey));
        }

        public static ResumePlan Create(Guid profileId, WatchStateEntry entry)
        {
            if (profileId == Guid.Empty ||
                entry == null ||
                entry.ProfileId != profileId ||
                entry.IsCompleted ||
                string.IsNullOrWhiteSpace(entry.CanonicalKey))
            {
                return null;
            }

            double position = entry.PositionSeconds;
            bool validPosition =
                !double.IsNaN(position) && !double.IsInfinity(position) &&
                position > 0 &&
                (entry.DurationSeconds <= 0 || position < entry.DurationSeconds);

            return new ResumePlan(profileId, entry, validPosition ? (double?)position : null);
        }

        // The currently available source matching the saved one, or null if
        // there is no saved source or nothing matches (caller then shows the
        // Source Selector).
        public static NormalizedPlaybackSource FindSavedSource(
            ResumePlan plan,
            IEnumerable<NormalizedPlaybackSource> available)
        {
            if (plan == null || plan.SavedSource == null)
            {
                return null;
            }

            return plan.SavedSource.FindMatch(available);
        }
    }
}
