// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.IO;
using System.Threading.Tasks;

namespace SinePlayer.Common.Imaging;

internal interface IImageConnection : IDisposable
{
    string Path { get; }

    string CacheKey { get; }

    string Fingerprint { get; }

    Task<Stream?> OpenImageStream();

    IVectorImageService? OpenVectorService();
}
