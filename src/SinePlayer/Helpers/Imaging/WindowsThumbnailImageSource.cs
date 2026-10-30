// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.IO;
using System.Threading.Tasks;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Imaging;

using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace SinePlayer.Helpers.Imaging;

internal sealed partial class WindowsThumbnailImageSource : IImageSource
{
    private const string TAG = nameof(WindowsThumbnailImageSource);
    private const uint THUMBNAIL_SIZE = 400;

    private readonly string _path;
    private readonly string _uri;
    private readonly ThumbnailMode _thumbnailMode;
    private readonly ImageLoaderSchedulerGroup _preferredSchedulerGroup;

    public WindowsThumbnailImageSource(string path, string uri, ThumbnailMode thumbnailMode)
    {
        _path = path;
        _uri = uri;
        _thumbnailMode = thumbnailMode;
        _preferredSchedulerGroup = ImageLoaderSchedulerGroup.FromPath(path);
    }

    public string Uri => _uri;

    public ImageLoaderSchedulerGroup PreferredSchedulerGroup => _preferredSchedulerGroup;

    public ImageCacheValidationMode CacheValidationMode => ImageCacheValidationMode.Deferred;

    public Task<IImageConnection?> Open()
    {
        return Task.FromResult<IImageConnection?>(new Connection(_path, _uri, _thumbnailMode));
    }

    private static async Task<byte[]?> ReadThumbnail(string path, ThumbnailMode thumbnailMode)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(thumbnailMode, THUMBNAIL_SIZE, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null)
            {
                return null;
            }

            using (thumbnail)
            {
                ulong size = thumbnail.Size;
                if (size == 0)
                {
                    return null;
                }

                using DataReader reader = new(thumbnail.GetInputStreamAt(0));
                await reader.LoadAsync((uint)size);
                byte[] bytes = new byte[(int)size];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"Failed to read the thumbnail of '{path}'", ex);
            return null;
        }
    }

    private sealed partial class Connection(string path, string uri, ThumbnailMode thumbnailMode) : IImageConnection
    {
        public string Path => path;

        public string CacheKey => uri;

        public string Fingerprint => FileUtils.GetFileSignature(path);

        public void Dispose()
        {
        }

        public async Task<Stream?> OpenImageStream()
        {
            byte[]? bytes = await ReadThumbnail(path, thumbnailMode);
            return bytes is null ? null : new MemoryStream(bytes, writable: false);
        }

        public IVectorImageService? OpenVectorService()
        {
            return null;
        }
    }
}
