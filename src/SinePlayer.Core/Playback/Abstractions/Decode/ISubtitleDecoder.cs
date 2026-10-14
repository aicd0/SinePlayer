// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// Turns encoded subtitle packets into cues. Subtitle rendering is not implemented yet; the
/// contract exists so the pipeline and the registry already carry a slot for it.
/// </summary>
public interface ISubtitleDecoder : IMediaDecoder
{
    PlaybackResult TryDecode(MediaPacket packet, out IReadOnlyList<SubtitleCue> cues);

    PlaybackResult Drain(out IReadOnlyList<SubtitleCue> cues);
}
