// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public interface IMediaDecoder : IDisposable
{
    MediaStreamInfo Stream { get; }

    bool IsHardwareAccelerated { get; }

    PlaybackOperationResult Flush();
}
