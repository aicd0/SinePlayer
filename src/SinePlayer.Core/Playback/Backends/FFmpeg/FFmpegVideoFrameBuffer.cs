// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Owns one decoded <c>AVFrame</c>. The frame is moved out of the decoder rather than referenced, so
/// the decoder is immediately ready for the next one while the consumer keeps the pixels.
/// </summary>
internal sealed class FFmpegVideoFrameBuffer : IPlanarVideoBuffer
{
    private readonly int _planeCount;

    private nint _frame;

    internal FFmpegVideoFrameBuffer(nint frame, int planeCount)
    {
        _frame = frame;
        _planeCount = planeCount;
    }

    public int PlaneCount => _planeCount;

    /// <summary>Frames are described by planes, so a single byte count would be meaningless.</summary>
    public long Length => 0;

    public unsafe nint GetPlanePointer(int plane)
    {
        nint frame = _frame;
        if (frame == 0 || plane < 0 || plane >= _planeCount)
        {
            return 0;
        }

        return (nint)((AVFrame*)frame)->Data[plane];
    }

    public unsafe int GetPlaneStride(int plane)
    {
        nint frame = _frame;
        if (frame == 0 || plane < 0 || plane >= _planeCount)
        {
            return 0;
        }

        return ((AVFrame*)frame)->LineSize[plane];
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
}
