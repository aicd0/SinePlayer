// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Registers libavcodec as a software audio decoder. Like its video counterpart it restricts itself to
/// no codec, because FFmpeg covers far more than the engine could enumerate and selection is driven by
/// whatever the container reports. Resampling is not its job: it hands out the decoder's own format.
/// </summary>
public sealed class FFmpegAudioDecoderFactory : IAudioDecoderFactory
{
    public PlaybackOperationResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IComponentCreator<IAudioDecoder>? creator)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        creator = new Creator(source, stream);
        return PlaybackOperationResult.Success;
    }

    /// <summary>One backend, one kind of decoder, so the signature says nothing more than which backend it is.</summary>
    private sealed class Creator(IMediaSource source, AudioStreamInfo stream) : IComponentCreator<IAudioDecoder>
    {
        public string Signature => "ffmpeg:audio";

        public PlaybackOperationResult Create(out IAudioDecoder? component)
        {
            component = null;

            if (!FFmpegAudioDecoder.TryCreate(source, stream, out FFmpegAudioDecoder? ffmpegDecoder, out string error))
            {
                return PlaybackOperationResult.Failure(error);
            }

            component = ffmpegDecoder;
            return PlaybackOperationResult.Success;
        }
    }
}
