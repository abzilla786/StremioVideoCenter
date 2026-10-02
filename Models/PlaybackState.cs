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
        // True only for a genuine normal end of playback (mpv's
        // eof-reached property, or end-file reason=eof) - never for
        // error/stop/quit/redirect end-file reasons.
        public bool ReachedEndOfFile { get; set; }

        // True once mpv has confirmed the selected file loaded
        // (file-loaded). Reset to false before every new loadfile.
        public bool IsPlaybackEstablished { get; set; }

        // True once playback is established AND a numeric time-pos has
        // been read since (and no resume seek is still pending). Only then
        // is PositionSeconds a real position - 0 is a legitimate value
        // when this is true, and meaningless when it is false.
        public bool HasValidPosition { get; set; }

        // Incremented on every user-issued seek, so persistence can flush
        // immediately on a seek without inferring it from position jumps.
        public int SeekCounter { get; set; }

        // Last mpv end-file reason and file_error (null if absent), kept
        // for diagnostics/failure handling. Contains no URL.
        public string EndFileReason { get; set; }
        public string EndFileError { get; set; }

        public IReadOnlyList<MpvTrackInfo> AudioTracks { get; set; }
        public IReadOnlyList<MpvTrackInfo> SubtitleTracks { get; set; }

        // mpv's sub-delay property in seconds (positive = subtitles appear
        // later). Session-only: never persisted, never part of media identity.
        public double SubtitleDelaySeconds { get; set; }

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
