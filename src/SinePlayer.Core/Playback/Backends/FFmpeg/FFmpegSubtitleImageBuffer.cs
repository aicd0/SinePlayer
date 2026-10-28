// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

internal sealed partial class FFmpegSubtitleImageBuffer : ISubtitleImageBuffer
{
    private nint _pixels;

    internal FFmpegSubtitleImageBuffer(nint pixels, int width, int height, int stride)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public nint Pixels => _pixels != 0 ? _pixels : throw new ObjectDisposedException(nameof(FFmpegSubtitleImageBuffer));

    public long Length => Stride * Height;

    public void Dispose()
    {
        nint pixels = _pixels;
        if (pixels == 0)
        {
            return;
        }

        _pixels = 0;
        Marshal.FreeHGlobal(pixels);
    }
}
