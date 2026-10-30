// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using SinePlayer.Core.Imaging;

namespace SinePlayer.Common.Storage;

internal sealed class ResourceUri
{
    public const string SCHEME = "res://";

    private const string SCHEME_NAME = "res";

    private static readonly IResourceUriInterceptor[] sInterceptors =
    [
        new ResourceFileUriInterceptor(),
        new MediaThumbnailUriInterceptor(),
    ];

    public static ResourceUri CreateResourceFile(string resourceId, string fileName)
    {
        return new(new ResourceFileUriInterceptor.Handler(resourceId, fileName));
    }

    public static ResourceUri CreateMediaThumbnail(long mediaId)
    {
        return new(new MediaThumbnailUriInterceptor.Handler(mediaId));
    }

    private readonly IResourceUriHandler _handler;
    private readonly Lazy<Uri> _uri;

    private ResourceUri(IResourceUriHandler handler)
    {
        _handler = handler;
        _uri = new(() => _handler.Uri);
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out ResourceUri? result)
    {
        result = null;
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !uri.Scheme.Equals(SCHEME_NAME, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (IResourceUriInterceptor interceptor in sInterceptors)
        {
            if (interceptor.TryParse(uri, out IResourceUriHandler? handler))
            {
                result = new ResourceUri(handler);
                return true;
            }
        }

        return false;
    }

    public Task<IImageSource?> ResolveImage()
    {
        return _handler.ResolveImage();
    }

    public Task Release()
    {
        return _handler.Release();
    }

    public override string ToString()
    {
        return _uri.Value.ToString();
    }
}
