// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.IO;
using System.Threading.Tasks;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Imaging;

namespace SinePlayer.Helpers.Imaging;

internal sealed partial class LocalFileImageSource : IImageSource
{
    private const string TAG = nameof(LocalFileImageSource);

    private readonly string _path;
    private readonly string _uri;
    private readonly ImageLoaderSchedulerGroup _preferredSchedulerGroup;

    public LocalFileImageSource(string path, string? uri = null)
    {
        _path = path;
        _uri = string.IsNullOrEmpty(uri) ? path : uri;
        _preferredSchedulerGroup = ImageLoaderSchedulerGroup.FromPath(path);
    }

    public string Uri => _uri;

    public ImageLoaderSchedulerGroup PreferredSchedulerGroup => _preferredSchedulerGroup;

    public ImageCacheValidationMode CacheValidationMode => ImageCacheValidationMode.Deferred;

    public Task<IImageConnection?> Open()
    {
        return Task.FromResult<IImageConnection?>(new LocalFileImageConnection(_path));
    }

    private sealed partial class LocalFileImageConnection(string path) : IImageConnection
    {
        public string Path => path;

        public string CacheKey => path;

        public string Fingerprint => FileUtils.GetFileSignature(path);

        public void Dispose()
        {
        }

        public Task<Stream?> OpenImageStream()
        {
            Stream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                Logger.E(TAG, $"Failed to open image '{path}'", ex);
            }

            return Task.FromResult(stream);
        }

        public IVectorImageService? OpenVectorService()
        {
            return null;
        }
    }
}
