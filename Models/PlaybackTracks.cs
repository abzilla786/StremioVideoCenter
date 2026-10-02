using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioVideoCenter.Models
{
    // The ONLY place PlaybackState's track fields change (pure, no mpv/WPF).
    //
    // mpv owns tracks. These helpers just mirror what mpv reports into
    // PlaybackState, and clear it when a new file is loaded: track ids are
    // local to the loaded file, so a list/selection/delay from the previous
    // episode or source must never survive into the next one.
    public static class PlaybackTracks
    {
        // A new file is being loaded: forget everything about the old one.
        // TracksLoaded stays false until mpv reports the new file's list.
        public static void Reset(PlaybackState state)
        {
            state.AudioTracks = new List<MpvTrackInfo>();
            state.SubtitleTracks = new List<MpvTrackInfo>();
            state.SelectedAudioTrackId = null;
            state.SelectedSubtitleTrackId = null;
            state.SubtitleDelaySeconds = 0;
            state.TracksLoaded = false;
            state.TracksVersion++;
        }

        // mpv's "track-list": replaces both lists. The selected tracks are
        // read from the list's own "selected" flags (mpv's truth) - a track
        // type with nothing selected (e.g. subtitles Off) has no selection.
        public static void ApplyTrackList(PlaybackState state, IEnumerable<MpvTrackInfo> tracks)
        {
            List<MpvTrackInfo> all = (tracks ?? Enumerable.Empty<MpvTrackInfo>())
                .Where(t => t != null)
                .ToList();

            List<MpvTrackInfo> audio = all
                .Where(t => string.Equals(t.Type, "audio", StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<MpvTrackInfo> subs = all
                .Where(t => string.Equals(t.Type, "sub", StringComparison.OrdinalIgnoreCase))
                .ToList();

            state.AudioTracks = audio;
            state.SubtitleTracks = subs;

            MpvTrackInfo selectedAudio = audio.FirstOrDefault(t => t.IsSelected);
            MpvTrackInfo selectedSub = subs.FirstOrDefault(t => t.IsSelected);

            state.SelectedAudioTrackId = selectedAudio != null ? (int?)selectedAudio.Id : null;
            state.SelectedSubtitleTrackId = selectedSub != null ? (int?)selectedSub.Id : null;

            // Only a list read from an ESTABLISHED file counts as "loaded":
            // while the new file is still loading mpv can also report the
            // previous file's tracks going away (an empty list), which must
            // not be mistaken for "this file has no tracks".
            state.TracksLoaded = state.IsPlaybackEstablished;
            state.TracksVersion++;
        }

        // mpv's "aid"/"sid" properties (null = no track selected / "no").
        public static void ApplySelectedAudio(PlaybackState state, int? trackId)
        {
            if (state.SelectedAudioTrackId != trackId)
            {
                state.SelectedAudioTrackId = trackId;
                state.TracksVersion++;
            }
        }

        public static void ApplySelectedSubtitle(PlaybackState state, int? trackId)
        {
            if (state.SelectedSubtitleTrackId != trackId)
            {
                state.SelectedSubtitleTrackId = trackId;
                state.TracksVersion++;
            }
        }

        // mpv's "sub-delay". Invalid values (null, NaN, infinity) read as 0:
        // the UI must never show a delay mpv is not actually applying.
        public static void ApplySubtitleDelay(PlaybackState state, double? seconds)
        {
            state.SubtitleDelaySeconds =
                seconds.HasValue && !double.IsNaN(seconds.Value) && !double.IsInfinity(seconds.Value)
                    ? seconds.Value
                    : 0;
        }

        // A selection may only target a track that exists in the CURRENT
        // file's list - never a stale id from a previous file.
        public static bool HasAudioTrack(PlaybackState state, int trackId)
        {
            return state.AudioTracks != null && state.AudioTracks.Any(t => t.Id == trackId);
        }

        public static bool HasSubtitleTrack(PlaybackState state, int trackId)
        {
            return state.SubtitleTracks != null && state.SubtitleTracks.Any(t => t.Id == trackId);
        }

        // The value for mpv's "sid" property: a track id, or "no" for Off.
        public static object SubtitleSelectionValue(int? trackId)
        {
            return trackId.HasValue ? (object)trackId.Value : "no";
        }
    }
}
