// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

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
    public ComponentCapabilities Capabilities { get; } = new()
    {
        Name = "ffmpeg-software-audio",
        Description = "FFmpeg software audio decoder.",
        IsHardwareAccelerated = false,
    };

    public PlaybackResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IAudioDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!FFmpegAudioDecoder.TryCreate(source, stream, out FFmpegAudioDecoder? ffmpegDecoder, out string error))
        {
            decoder = null;
            return PlaybackResult.Failure(error);
        }

        decoder = ffmpegDecoder;
        return PlaybackResult.Success;
    }
}
