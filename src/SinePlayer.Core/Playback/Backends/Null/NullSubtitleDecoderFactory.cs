// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// Offers <see cref="NullSubtitleDecoder"/>s. It never refuses: a decoder that produces nothing can serve any track,
/// whatever its format — which is what makes it a stand-in a host or a test can register behind the real one, and what
/// makes a switch of subtitle decoder observable without a second real backend.
/// </summary>
public sealed class NullSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    private readonly string _signature;

    public NullSubtitleDecoderFactory(string signature = "null:subtitle-decoder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult TryCreate(IMediaSource? source, SubtitleStreamInfo stream, out IComponentCreator<ISubtitleDecoder>? creator)
    {
        ArgumentNullException.ThrowIfNull(stream);

        creator = new Creator(stream, _signature);
        return OperationResult.Success();
    }

    private sealed class Creator(SubtitleStreamInfo stream, string signature) : IComponentCreator<ISubtitleDecoder>
    {
        public string Signature => signature;

        public OperationResult Create(out ISubtitleDecoder? component)
        {
            component = new NullSubtitleDecoder(stream);
            return OperationResult.Success();
        }
    }
}
