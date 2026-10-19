// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Common.Imaging;

internal interface IImageResultHandler
{
    public void OnSuccess(DecodedImageModel result);

    public void OnFailure();
}
