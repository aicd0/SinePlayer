// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Registers libavcodec as a subtitle decoder. Like its audio and video counterparts it restricts itself to no codec,
/// because selection is driven by whatever the container reports. Bitmap formats are refused here rather than when the
/// first cue arrives: a track that cannot be shown should never be offered, so that the next candidate gets its turn.
/// </summary>
public sealed class FFmpegSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    public OperationResult TryCreate(IMediaSource? source, SubtitleStreamInfo stream, out IComponentCreator<ISubtitleDecoder>? creator)
    {
        ArgumentNullException.ThrowIfNull(stream);

        creator = null;
        if (stream.IsBitmap)
        {
            return OperationResult.Failure($"Bitmap subtitles ('{stream.CodecId}') are not supported yet.");
        }

        creator = new Creator(source, stream);
        return OperationResult.Success();
    }

    /// <summary>One backend, one kind of decoder, so the signature says nothing more than which backend it is.</summary>
    private sealed class Creator(IMediaSource? source, SubtitleStreamInfo stream) : IComponentCreator<ISubtitleDecoder>
    {
        public string Signature => "ffmpeg:subtitle";

        public OperationResult Create(out ISubtitleDecoder? component)
        {
            component = null;

            OperationResult<FFmpegSubtitleDecoder> decoder = FFmpegSubtitleDecoder.TryCreate(source, stream);
            if (!decoder.IsSuccessful)
            {
                return OperationResult.Failure(decoder);
            }

            component = decoder.Result;
            return OperationResult.Success();
        }
    }
}
