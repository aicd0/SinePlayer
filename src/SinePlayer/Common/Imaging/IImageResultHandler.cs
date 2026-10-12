// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Common.Imaging;

internal interface IImageResultHandler
{
    public void OnSuccess(DecodedImageModel result);

    public void OnFailure();
}
