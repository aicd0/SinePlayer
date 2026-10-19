// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading.Tasks;

using SinePlayer.Common.Imaging;

namespace SinePlayer.Common.Storage;

internal interface IResourceUriHandler
{
    Uri Uri { get; }

    Task<IImageSource?> ResolveImage();

    Task Release();
}
