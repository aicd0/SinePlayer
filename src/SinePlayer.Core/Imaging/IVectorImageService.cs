// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.Graphics.Canvas;

using Windows.Graphics.Imaging;

namespace SinePlayer.Core.Imaging;

public interface IVectorImageService : IDisposable
{
    Task<SoftwareBitmap?> CreateSoftwareBitmap(int width, int height);

    Task<CanvasBitmap?> CreateImageCanvasBitmap(ICanvasResourceCreator creator, int width, int height);
}
