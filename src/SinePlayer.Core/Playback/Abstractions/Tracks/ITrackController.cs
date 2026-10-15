// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Tracks;

/// <summary>
/// Enumerates the streams of the opened media and changes which ones are active. Selecting a stream
/// only affects the chain of that kind, so switching audio does not disturb the video path.
/// </summary>
public interface ITrackController
{
    MediaInfo Info { get; }

    VideoStreamInfo? SelectedVideoStream { get; }

    AudioStreamInfo? SelectedAudioStream { get; }

    SubtitleStreamInfo? SelectedSubtitleStream { get; }

    /// <summary>
    /// Activates the given stream. Passing null turns the corresponding kind off, which is how a
    /// caller disables audio or subtitles.
    /// </summary>
    PlaybackOperationResult SelectStream(MediaStreamInfo? stream);
}
