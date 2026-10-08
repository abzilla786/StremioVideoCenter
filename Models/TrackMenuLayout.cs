using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    public enum TrackMenuItemKind
    {
        Off,
        Track,
        DelayDecrease,
        DelayIncrease,
        DelayReset
    }

    // One FOCUSABLE entry of a track menu. (The delay value itself is not an
    // item: it has no action, so it can never take focus.)
    public sealed class TrackMenuItem
    {
        public TrackMenuItemKind Kind { get; set; }
        public int? TrackId { get; set; }
        public string Label { get; set; }
        public bool IsSelected { get; set; }
    }

    // What an Audio or Subtitles menu contains and in which focus order,
    // decided from mpv's state alone (pure; TrackSelectionMenu renders it).
    //
    //   Audio:      [track ...]
    //   Subtitles:  Off, [track ...], delay -, delay +, Reset
    //   Loading:    only a status line (+ the delay row for Subtitles)
    public sealed class TrackMenuLayout
    {
        public List<TrackMenuItem> Items { get; private set; }

        // "Loading tracks...", "No audio tracks available." or null.
        public string StatusMessage { get; private set; }

        public bool HasDelayRow { get; private set; }

        public double DelaySeconds { get; private set; }

        // Index into Items of the item focus should land on (the selected
        // track, or Off when subtitles are off); -1 = first item.
        public int InitialIndex { get; private set; }

        private TrackMenuLayout()
        {
            Items = new List<TrackMenuItem>();
            InitialIndex = -1;
        }

        public static TrackMenuLayout Build(
            bool isSubtitles,
            IReadOnlyList<MpvTrackInfo> tracks,
            int? selectedTrackId,
            bool tracksLoaded,
            double delaySeconds)
        {
            TrackMenuLayout layout = new TrackMenuLayout
            {
                HasDelayRow = isSubtitles,
                DelaySeconds = delaySeconds
            };

            if (!tracksLoaded)
            {
                layout.StatusMessage = "Loading tracks…";
            }
            else
            {
                IReadOnlyList<MpvTrackInfo> available = tracks ?? new List<MpvTrackInfo>();
                IReadOnlyList<string> labels = TrackLabels.DescribeAll(available);

                if (isSubtitles)
                {
                    layout.Add(new TrackMenuItem
                    {
                        Kind = TrackMenuItemKind.Off,
                        Label = "Off",
                        IsSelected = selectedTrackId == null
                    });
                }

                for (int i = 0; i < available.Count; i++)
                {
                    layout.Add(new TrackMenuItem
                    {
                        Kind = TrackMenuItemKind.Track,
                        TrackId = available[i].Id,
                        Label = labels[i],
                        IsSelected = selectedTrackId.HasValue && selectedTrackId.Value == available[i].Id
                    });
                }

                if (available.Count == 0)
                {
                    layout.StatusMessage = isSubtitles
                        ? "No subtitle tracks available."
                        : "No audio tracks available.";
                }
            }

            if (isSubtitles)
            {
                // sub-delay is an mpv option independent of the track list, so
                // the delay controls exist even while loading / with no tracks.
                layout.Items.Add(new TrackMenuItem { Kind = TrackMenuItemKind.DelayDecrease });
                layout.Items.Add(new TrackMenuItem { Kind = TrackMenuItemKind.DelayIncrease });
                layout.Items.Add(new TrackMenuItem { Kind = TrackMenuItemKind.DelayReset });
            }

            return layout;
        }

        private void Add(TrackMenuItem item)
        {
            if (item.IsSelected && InitialIndex < 0)
            {
                InitialIndex = Items.Count;
            }

            Items.Add(item);
        }
    }
}
