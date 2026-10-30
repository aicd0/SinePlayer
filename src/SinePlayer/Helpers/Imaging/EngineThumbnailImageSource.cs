// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

using SinePlayer.Common.Imaging;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Thumbnails;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SinePlayer.Helpers.Imaging;

internal sealed partial class EngineThumbnailImageSource : IImageSource
{
    private const string TAG = nameof(EngineThumbnailImageSource);
    private const int THUMBNAIL_MAX_EDGE = 400;

    private static readonly ConcurrentDictionary<string, byte> sFailures = [];

    private readonly string _path;
    private readonly string _uri;
    private readonly ImageLoaderSchedulerGroup _preferredSchedulerGroup;

    public EngineThumbnailImageSource(string path, string uri)
    {
        _path = path;
        _uri = uri;
        _preferredSchedulerGroup = ImageLoaderSchedulerGroup.FromPath(path);
    }

    public string Uri => _uri;

    public ImageLoaderSchedulerGroup PreferredSchedulerGroup => _preferredSchedulerGroup;

    public ImageCacheValidationMode CacheValidationMode => ImageCacheValidationMode.Deferred;

    public Task<IImageConnection?> Open()
    {
        return Task.FromResult<IImageConnection?>(new Connection(_path, _uri));
    }

    private static async Task<byte[]?> ExtractPng(string path)
    {
        string key = path + "|" + FileUtils.GetFileSignature(path);
        if (sFailures.ContainsKey(key))
        {
            return null;
        }

        try
        {
            FFmpegRuntime.EnsureInitialized();

            OperationResult<MediaThumbnail> extracted = MediaThumbnailExtractor.TryExtract(
                new FFmpegSourceProviderFactory(),
                new FFmpegVideoDecoderFactory(),
                MediaLocator.Parse(path),
                position: null,
                THUMBNAIL_MAX_EDGE);
            if (!extracted.IsSuccessful)
            {
                Logger.W(TAG, $"Unable to draw a thumbnail for '{path}': {extracted.ErrorMessage}");
                sFailures.TryAdd(key, 0);
                return null;
            }

            return await EncodePng(extracted.Result);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"Failed to draw a thumbnail for '{path}'", ex);
            sFailures.TryAdd(key, 0);
            return null;
        }
    }

    private static async Task<byte[]> EncodePng(MediaThumbnail thumbnail)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)thumbnail.Width,
            (uint)thumbnail.Height,
            96.0,
            96.0,
            thumbnail.Pixels);
        await encoder.FlushAsync();

        byte[] bytes = new byte[stream.Size];
        using (DataReader reader = new(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        return bytes;
    }

    private sealed partial class Connection(string path, string uri) : IImageConnection
    {
        public string Path => path;

        public string CacheKey => uri;

        public string Fingerprint => FileUtils.GetFileSignature(path);

        public void Dispose()
        {
        }

        public async Task<Stream?> OpenImageStream()
        {
            byte[]? png = await ExtractPng(path);
            return png is null ? null : new MemoryStream(png, writable: false);
        }

        public IVectorImageService? OpenVectorService()
        {
            return null;
        }
    }
}
