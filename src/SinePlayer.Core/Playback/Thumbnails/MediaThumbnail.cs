// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Thumbnails;

public sealed class MediaThumbnail
{
    internal MediaThumbnail(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        Stride = width * 4;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }
}
