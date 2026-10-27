// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Registers libavcodec as a video decoder. It restricts itself to no codec because FFmpeg covers far more than
/// the engine could enumerate, so a stream it cannot decode fails when it is asked to decode it, and the next
/// registered decoder is offered its turn.
/// <para>
/// It decodes on the GPU only when asked to and only when the codec has a decoder there; asked to and unable, it
/// fails rather than quietly copying frames back to memory, because the request is what tells the caller what it
/// is going to get. Giving it up is the resolver's decision, and it makes it by asking again without it.
/// </para>
/// </summary>
public sealed class FFmpegVideoDecoderFactory : IVideoDecoderFactory
{
    public OperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IComponentCreator<IVideoDecoder>? creator)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        creator = new Creator(source, stream, options);
        return OperationResult.Success();
    }

    /// <summary>
    /// What libavcodec would be asked for, decided from the options alone — which is the point: the signature says
    /// whether this is a change, and opening a decoder to answer that would defeat it.
    /// </summary>
    private sealed class Creator(IMediaSource source, VideoStreamInfo stream, DecoderOptions options) : IComponentCreator<IVideoDecoder>
    {
        public string Signature => options.HardwareDecoding ? "ffmpeg:video:hw" : "ffmpeg:video:sw";

        public OperationResult Create(out IVideoDecoder? component)
        {
            component = null;

            // Frames stay on the GPU only when the decoder has the pipeline's device: a texture is readable by the
            // device that wrote it, so one decoded on a device of its own would have to be copied back first.
            bool gpuFrames = options.GpuDevice is not null;
            OperationResult<FFmpegVideoDecoder> decoder = FFmpegVideoDecoder.TryCreate(source, stream, options.HardwareDecoding, gpuFrames, options.GpuDevice, options.BufferedFrames);
            if (!decoder.IsSuccessful)
            {
                return OperationResult.Failure(decoder);
            }

            component = decoder.Result;
            return OperationResult.Success();
        }
    }
}