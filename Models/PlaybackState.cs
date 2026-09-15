using System.Collections.Generic;

namespace StremioVideoCenter.Models
{
    public enum PlayerMenu
    {
        None,
        Audio,
        Subtitles,
        Settings
    }

    // Ground-truth playback state. Kept in sync with mpv by PlayerSession via
    // property observation (pause/mute/volume/tracks) and polling (position/
    // duration only). UI code reads this; it never talks to mpv directly.
    public sealed class PlaybackState
    {
        public PlaybackState()
        {
            AudioTracks = new List<MpvTrackInfo>();
            SubtitleTracks = new List<MpvTrackInfo>();
        }

        public bool IsPlaying { get; set; }
        public double PositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public double Volume { get; set; }
        public bool IsMuted { get; set; }
        public bool ReachedEndOfFile { get; set; }

        public IReadOnlyList<MpvTrackInfo> AudioTracks { get; set; }
        public IReadOnlyList<MpvTrackInfo> SubtitleTracks { get; set; }

        public int? SelectedAudioTrackId { get; set; }

        // null = subtitles off.
        public int? SelectedSubtitleTrackId { get; set; }
    }

    // Local WPF-side UI concerns. Never sent to mpv, never received from it.
    public sealed class PlayerUiState
    {
        public bool ControlsVisible { get; set; }
        public PlayerMenu OpenMenu { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsSeekBarBeingDragged { get; set; }
    }
}
