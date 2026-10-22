// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Owns one decoded <c>AVFrame</c> that never left the GPU. The frame holds the reference to the texture
/// the decoder wrote, so keeping the frame alive is what keeps the picture alive, and releasing it is what
/// gives the texture back to the decoder's pool.
/// </summary>
internal sealed partial class FFmpegGpuVideoFrameBuffer : IGpuVideoBuffer
{
    private nint _frame;

    internal FFmpegGpuVideoFrameBuffer(nint frame)
    {
        _frame = frame;
    }

    /// <summary>A texture has no byte count of its own, and its layout is described by the pixel format.</summary>
    public long Length => 0;

    public unsafe nint NativeTexture
    {
        get
        {
            nint frame = _frame;
            return frame == 0 ? 0 : (nint)((AVFrame*)frame)->Data[0];
        }
    }

    /// <summary>
    /// The slice of the texture the frame was decoded into. libavcodec stores it in <c>data[1]</c> as a
    /// number rather than a pointer.
    /// </summary>
    public unsafe int SubresourceIndex
    {
        get
        {
            nint frame = _frame;
            return frame == 0 ? 0 : (int)((AVFrame*)frame)->Data[1];
        }
    }

    public void Dispose()
    {
        nint frame = _frame;
        if (frame == 0)
        {
            return;
        }

        _frame = 0;
        FFmpegNative.av_frame_free(ref frame);
    }

    /// <summary>
    /// A second reference to the same picture. libavcodec counts references to the pool slice, so holding one
    /// is what stops the decoder from handing that slice out again while a renderer still draws it.
    /// </summary>
    public IGpuVideoBuffer? Retain()
    {
        nint frame = _frame;
        if (frame == 0)
        {
            return null;
        }

        nint copy = FFmpegNative.av_frame_alloc();
        if (copy == 0)
        {
            return null;
        }

        int result = FFmpegNative.av_frame_ref(copy, frame);
        if (result < 0)
        {
            FFmpegNative.av_frame_free(ref copy);
            return null;
        }

        return new FFmpegGpuVideoFrameBuffer(copy);
    }
}
