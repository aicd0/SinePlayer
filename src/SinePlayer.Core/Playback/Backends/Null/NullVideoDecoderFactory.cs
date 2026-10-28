// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed class NullVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly string _signature;

    public NullVideoDecoderFactory(string signature = "null:video-decoder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult<IComponentCreator<IVideoDecoder>> Create(IMediaSource source, VideoStreamInfo stream, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        return new OperationResultCreator<IComponentCreator<IVideoDecoder>>().Success(new Creator(stream, _signature));
    }

    private sealed class Creator(VideoStreamInfo stream, string signature) : IComponentCreator<IVideoDecoder>
    {
        public string Signature => signature;

        public OperationResult Create(out IVideoDecoder? component)
        {
            component = new NullVideoDecoder(stream);
            return OperationResult.Success();
        }
    }
}
