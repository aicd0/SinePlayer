// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Imaging;
using SinePlayerUWP.Common.Misc;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Helpers.Imaging;

using Windows.Storage.FileProperties;

namespace SinePlayerUWP.Common.Storage;

internal sealed class MediaImageUriInterceptor : IResourceUriInterceptor
{
    private const string HOST = "MediaImage";
    private const string QUERY_MEDIA_ID = "id";
    private const string QUERY_INDEX = "index";

    public bool TryParse(Uri uri, [NotNullWhen(true)] out IResourceUriHandler? handler)
    {
        handler = null;
        if (!uri.Host.Equals(HOST, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string mediaIdRaw = string.Empty;
        string indexRaw = string.Empty;
        foreach (string item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = item.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = item[..equalsIndex];
            string itemValue = item[(equalsIndex + 1)..];
            if (key.Equals(QUERY_MEDIA_ID, StringComparison.OrdinalIgnoreCase))
            {
                mediaIdRaw = itemValue;
            }
            else if (key.Equals(QUERY_INDEX, StringComparison.OrdinalIgnoreCase))
            {
                indexRaw = itemValue;
            }
        }

        if (!long.TryParse(mediaIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mediaId))
        {
            return false;
        }

        if (!int.TryParse(indexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0)
        {
            return false;
        }

        handler = new Handler(mediaId, index);
        return true;
    }

    public sealed class Handler : IResourceUriHandler
    {
        private readonly Uri _uri;

        public Handler(long mediaId, int index)
        {
            MediaId = mediaId;
            Index = index;
            string idText = mediaId.ToString(CultureInfo.InvariantCulture);
            string indexText = index.ToString(CultureInfo.InvariantCulture);
            _uri = new Uri($"{ResourceUri.SCHEME}{HOST}?{QUERY_MEDIA_ID}={idText}&{QUERY_INDEX}={indexText}");
        }

        public long MediaId { get; }

        public int Index { get; }

        public Uri Uri => _uri;

        public async Task<IImageSource?> ResolveImage()
        {
            MediaModel? media = await MediaModel.FromId(MediaId);
            if (media is null || media.IsCollection)
            {
                return null;
            }

            string? coverImage = media.GetExt(MediaExt.COVER_IMAGE);
            if (!string.IsNullOrEmpty(coverImage) && File.Exists(coverImage))
            {
                return new LocalFileImageSource(coverImage, _uri.ToString());
            }

            string location = media.Location;
            string extension = Path.GetExtension(location);
            bool isAudioExtension = AppInfoProvider.IsSupportedAudioExtension(extension);
            if (string.IsNullOrEmpty(location) || !File.Exists(location) ||
                !(isAudioExtension || AppInfoProvider.IsSupportedVideoExtension(extension)))
            {
                return null;
            }

            ThumbnailMode thumbnailMode = isAudioExtension ? ThumbnailMode.MusicView : ThumbnailMode.VideosView;
            return new MediaThumbnailImageSource(location, _uri.ToString(), thumbnailMode);
        }

        public Task Release()
        {
            return Task.CompletedTask;
        }
    }
}
