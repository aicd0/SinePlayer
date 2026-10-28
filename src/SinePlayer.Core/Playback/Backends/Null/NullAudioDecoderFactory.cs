// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// Offers <see cref="NullAudioDecoder"/>s. Like its video counterpart it never refuses, so registering it behind
/// a real decoder makes it the fallback for streams nothing else can serve.
/// </summary>
public sealed class NullAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly string _signature;

    public NullAudioDecoderFactory(string signature = "null:audio-decoder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        return new OperationResultCreator<IComponentCreator<IAudioDecoder>>().Success(new Creator(stream, _signature));
    }

    private sealed class Creator(AudioStreamInfo stream, string signature) : IComponentCreator<IAudioDecoder>
    {
        public string Signature => signature;

        public OperationResult Create(out IAudioDecoder? component)
        {
            component = new NullAudioDecoder(stream);
            return OperationResult.Success();
        }
    }
}
