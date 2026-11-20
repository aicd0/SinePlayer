// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Imaging;

public sealed partial class ImageContent(Stream stream, ImageMeta? meta) : IDisposable
{
    public Stream Stream { get; } = stream;

    public ImageMeta? Meta { get; } = meta;

    public void Dispose()
    {
        Stream.Dispose();
    }
}
