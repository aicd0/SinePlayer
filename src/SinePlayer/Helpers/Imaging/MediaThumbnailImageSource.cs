// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;

using SinePlayer.Common.Imaging;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;

using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace SinePlayer.Helpers.Imaging;

internal sealed partial class MediaThumbnailImageSource : IImageSource
{
    private const string TAG = nameof(MediaThumbnailImageSource);
    private const uint THUMBNAIL_SIZE = 400;

    private readonly string _path;
    private readonly string _uri;
    private readonly ThumbnailMode _thumbnailMode;
    private readonly ImageLoaderSchedulerGroup _preferredSchedulerGroup;

    public MediaThumbnailImageSource(string path, string uri, ThumbnailMode thumbnailMode)
    {
        _path = path;
        _uri = uri;
        _thumbnailMode = thumbnailMode;
        _preferredSchedulerGroup = ImageLoaderSchedulerGroup.FromPath(path);
    }

    public string Uri => _uri;

    public ImageLoaderSchedulerGroup PreferredSchedulerGroup => _preferredSchedulerGroup;

    public bool IsCacheValidationEnabled => true;

    public async Task<IImageConnection?> Open()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(_path);
            StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(_thumbnailMode, THUMBNAIL_SIZE, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                thumbnail?.Dispose();
                return null;
            }

            return new Connection(_path, _uri, thumbnail);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"Failed to open thumbnail for '{_path}'", ex);
            return null;
        }
    }

    private sealed partial class Connection(string path, string uri, StorageItemThumbnail thumbnail) : IImageConnection
    {
        public string Path => path;

        public string CacheKey => uri;

        public string Fingerprint => FileUtils.GetFileSignature(path);

        public void Dispose()
        {
            thumbnail.Dispose();
        }

        public async Task<Stream?> OpenImageStream()
        {
            try
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
                return new MemoryStream(bytes);
            }
            catch (Exception ex)
            {
                Logger.E(TAG, $"Failed to read thumbnail stream for '{path}'", ex);
                return null;
            }
        }

        public IVectorImageService? OpenVectorService()
        {
            return null;
        }
    }
}
