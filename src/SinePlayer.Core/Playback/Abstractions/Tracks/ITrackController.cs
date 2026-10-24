// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Tracks;

public interface ITrackController
{
    MediaInfo Info { get; }

    VideoStreamInfo? SelectedVideoStream { get; }

    AudioStreamInfo? SelectedAudioStream { get; }

    SubtitleStreamInfo? SelectedSubtitleStream { get; }

    OperationResult SelectStream(MediaStreamInfo? stream);
}
