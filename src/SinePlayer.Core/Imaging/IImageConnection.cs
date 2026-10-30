// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Imaging;

public interface IImageConnection : IDisposable
{
    string Path { get; }

    string CacheKey { get; }

    string Fingerprint { get; }

    Task<Stream?> OpenImageStream();

    IVectorImageService? OpenVectorService();
}
