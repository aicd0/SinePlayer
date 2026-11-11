// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal interface IChainFeed : IPlaybackChain
{
    bool IsConsuming { get; }

    ChainTail ReadTail { get; }

    void Accept(MediaStreamInfo stream, MediaPacket packet, ChainVersion versions, long epoch);

    void SignalEnd(ChainVersion versions, long epoch);

    void Select(StreamBinding<MediaStreamInfo>? binding);
}
