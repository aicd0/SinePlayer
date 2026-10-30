// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Imaging;

public interface IImageSource
{
    string Uri { get; }

    ImageLoaderSchedulerGroup PreferredSchedulerGroup { get; }

    ImageCacheValidationMode CacheValidationMode { get; }

    Task<IImageConnection?> Open();
}
