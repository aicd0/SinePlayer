// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Registers libavcodec as a software video decoder. It restricts itself to no codec because FFmpeg
/// covers far more than the engine could enumerate, and selection is driven by whatever the container
/// reports.
/// <para>
/// A hardware decoder registers alongside this one with a higher kind preference; this factory stays
/// the fallback, which is exactly what <c>ComponentSelectionOptions.AllowFallback</c> orders.
/// </para>
/// </summary>
public sealed class FFmpegVideoDecoderFactory : IVideoDecoderFactory
{
    public ComponentCapabilities Capabilities { get; } = new()
    {
        Name = "ffmpeg-software-video",
        Description = "FFmpeg software video decoder.",
        IsHardwareAccelerated = false,
    };

    public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!FFmpegVideoDecoder.TryCreate(source, stream, out FFmpegVideoDecoder? ffmpegDecoder, out string error))
        {
            decoder = null;
            return PlaybackOperationResult.Failure(error);
        }

        decoder = ffmpegDecoder;
        return PlaybackOperationResult.Success;
    }
}
