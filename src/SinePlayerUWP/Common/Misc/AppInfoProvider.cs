// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace SinePlayerUWP.Common.Misc;

internal static class AppInfoProvider
{
    private static readonly IReadOnlySet<string> _supportedImageExtensions = new HashSet<string>(
    [
        ".avif",
        ".bmp",
        ".gif",
        ".heic",
        ".heif",
        ".jpg",
        ".jpe",
        ".jpeg",
        ".png",
        ".tif",
        ".tiff",
        ".webp",
    ]);
    public static IReadOnlySet<string> SupportedImageExtensions => _supportedImageExtensions;

    public static IReadOnlySet<string> SupportedExternalFileExtensions => SupportedImageExtensions;

    public static bool IsSupportedExternalFileExtension(string extension)
    {
        return SupportedExternalFileExtensions.Contains(extension.ToLower());
    }

    public static bool IsSupportedImageExtension(string extension)
    {
        return SupportedImageExtensions.Contains(extension.ToLower());
    }
}
