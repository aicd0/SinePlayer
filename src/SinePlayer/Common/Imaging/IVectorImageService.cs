// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading.Tasks;

using Microsoft.Graphics.Canvas;

using Windows.Graphics.Imaging;

namespace SinePlayer.Common.Imaging;

internal interface IVectorImageService : IDisposable
{
    Task<SoftwareBitmap?> CreateSoftwareBitmap(int width, int height);

    Task<CanvasBitmap?> CreateImageCanvasBitmap(ICanvasResourceCreator creator, int width, int height);
}
