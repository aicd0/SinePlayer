// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed class NullSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    private readonly string _signature;

    public NullSubtitleDecoderFactory(string signature = "null:subtitle-decoder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult<IComponentCreator<ISubtitleDecoder>> Create(IMediaSource? source, SubtitleStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new OperationResultCreator<IComponentCreator<ISubtitleDecoder>>().Success(new Creator(stream, _signature));
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
