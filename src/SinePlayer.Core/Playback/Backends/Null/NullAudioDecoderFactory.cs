// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
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

    public PlaybackOperationResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IComponentCreator<IAudioDecoder>? creator)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        creator = new Creator(stream, _signature);
        return PlaybackOperationResult.Success;
    }

    private sealed class Creator(AudioStreamInfo stream, string signature) : IComponentCreator<IAudioDecoder>
    {
        public string Signature => signature;

        public PlaybackOperationResult Create(out IAudioDecoder? component)
        {
            component = new NullAudioDecoder(stream);
            return PlaybackOperationResult.Success;
        }
    }
}
