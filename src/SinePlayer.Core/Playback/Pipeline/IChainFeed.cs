// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal interface IChainFeed
{
    bool IsActive { get; }

    ChainTail Tail { get; }

    bool TryAccept(MediaSource source, MediaPacket packet, PipelineVersion versions, long epoch);

    void SignalEnd(PipelineVersion versions, long epoch);
}
