// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Owns one decoded audio <c>AVFrame</c>. The frame is moved out of the decoder rather than
/// referenced, so the decoder is immediately ready for the next one while the consumer keeps the
/// samples.
/// </summary>
internal sealed class FFmpegAudioFrameBuffer : IPlanarAudioBuffer
{
    private readonly int _planeCount;
    private readonly int _planeByteCount;

    private nint _frame;

    internal FFmpegAudioFrameBuffer(nint frame, int planeCount, int planeByteCount)
    {
        _frame = frame;
        _planeCount = planeCount;
        _planeByteCount = planeByteCount;
    }

    public int PlaneCount => _planeCount;

    public long Length => (long)_planeByteCount * _planeCount;

    public unsafe nint GetPlanePointer(int plane)
    {
        nint frame = _frame;
        if (frame == 0 || plane < 0 || plane >= _planeCount)
        {
            return 0;
        }

        var decoded = (AVFrame*)frame;

        // Channels past the inline array are only reachable through the extended data pointer, and
        // FFmpeg points it at the inline array too when there are few enough channels.
        nint planes = decoded->ExtendedData;
        return planes != 0 ? Marshal.ReadIntPtr(planes, plane * IntPtr.Size) : (nint)decoded->Data[plane];
    }

    public int GetPlaneByteCount(int plane)
    {
        return plane >= 0 && plane < _planeCount ? _planeByteCount : 0;
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
