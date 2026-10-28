// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

public sealed class FFmpegAudioDecoderFactory : IAudioDecoderFactory
{
    public OperationResult<IComponentCreator<IAudioDecoder>> Create(IMediaSource source, AudioStreamInfo stream, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        return new OperationResultCreator<IComponentCreator<IAudioDecoder>>().Success(new Creator(source, stream));
    }

    /// <summary>One backend, one kind of decoder, so the signature says nothing more than which backend it is.</summary>
    private sealed class Creator(IMediaSource source, AudioStreamInfo stream) : IComponentCreator<IAudioDecoder>
    {
        public string Signature => "ffmpeg:audio";

        public OperationResult Create(out IAudioDecoder? component)
        {
            component = null;

            OperationResult<FFmpegAudioDecoder> decoder = FFmpegAudioDecoder.Create(source, stream);
            if (!decoder.IsSuccessful)
            {
                return OperationResult.Failure(decoder);
            }

            component = decoder.Result;
            return OperationResult.Success();
        }
    }
}
