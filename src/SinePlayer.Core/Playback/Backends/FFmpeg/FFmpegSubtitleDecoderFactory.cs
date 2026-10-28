// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

public sealed class FFmpegSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    public OperationResult<IComponentCreator<ISubtitleDecoder>> Create(IMediaSource? source, SubtitleStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SubtitleCueFormat format = stream.IsBitmap ? SubtitleCueFormat.BitmapBgra : SubtitleCueFormat.Ass;
        return new OperationResultCreator<IComponentCreator<ISubtitleDecoder>>().Success(new Creator(source, stream, format));
    }

    private sealed class Creator(IMediaSource? source, SubtitleStreamInfo stream, SubtitleCueFormat format) : IComponentCreator<ISubtitleDecoder>
    {
        public string Signature => format == SubtitleCueFormat.BitmapBgra ? "ffmpeg:subtitle:bitmap" : "ffmpeg:subtitle:ass";

        public OperationResult Create(out ISubtitleDecoder? component)
        {
            component = null;

            OperationResult<FFmpegSubtitleDecoder> decoder = FFmpegSubtitleDecoder.Create(source, stream);
            if (!decoder.IsSuccessful)
            {
                return OperationResult.Failure(decoder);
            }

            component = decoder.Result;
            return OperationResult.Success();
        }
    }
}
