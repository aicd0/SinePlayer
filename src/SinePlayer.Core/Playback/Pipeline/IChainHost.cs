// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Pipeline;

internal interface IChainHost
{
    void OffsetChanged(IChainFeed chain);

    Task<OperationResult> Select(IPlaybackChain chain, MediaStreamInfo? stream);

    void PictureLayoutChanged(IPictureFrameSource picture);
}
