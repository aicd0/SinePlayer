// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Imaging;

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

        public Task<IImageSource?> ResolveImage()
        {
            // Cover images will be provided by the media thumbnail provider in a later migration phase.
            return Task.FromResult<IImageSource?>(null);
        }

        public Task Release()
        {
            return Task.CompletedTask;
        }
    }
}
