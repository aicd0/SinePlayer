// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

/// <summary>
/// The parts of a hardware frame that belong to FFmpeg rather than to any graphics API: where the frames context sits in
/// a codec context, and the layout the textures hold. Reading the layout here means the decoder learns what a hardware
/// frame is made of without knowing what it was decoded on.
/// </summary>
internal static class FFmpegHardwareFrames
{
    /// <summary><c>AV_PIX_FMT_NONE</c>.</summary>
    private const int PixelFormatNone = -1;

    /// <summary>
    /// <c>offsetof(AVCodecContext, hw_frames_ctx)</c> in the pinned build: the frames context libavcodec allocated for
    /// this decoder. The field sits far past the part of the context declared in <see cref="AVCodecContext"/>, so it is
    /// addressed by offset. That stays honest because FFmpeg only moves fields when it bumps the library major version,
    /// and <see cref="FFmpegRuntime"/> refuses to start with a build whose major version it was not written against.
    /// </summary>
    private const int FramesContextOffset = 552;

    /// <summary>
    /// The layout of the pixels inside a hardware frame. It is not the frame's own format — that is the hardware one —
    /// and not the decoder's software format either: it is what the frames context says the texture holds, which is what
    /// anything drawing it has to sample it as. False when there is no frames context, which is also how "this decoder
    /// never engaged the hardware" reads.
    /// </summary>
    internal static bool TryReadLayout(nint codecContext, out int layout)
    {
        layout = PixelFormatNone;

        nint reference = Marshal.ReadIntPtr(codecContext, FramesContextOffset);
        if (reference == 0)
        {
            return false;
        }

        AVBufferRef buffer = Marshal.PtrToStructure<AVBufferRef>(reference);
        AVHWFramesContext frames = Marshal.PtrToStructure<AVHWFramesContext>(buffer.Data);
        layout = frames.SoftwareFormat;
        return layout != PixelFormatNone;
    }
}
