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

    private static readonly IReadOnlySet<string> _supportedVideoExtensions = new HashSet<string>(
    [
        ".3gp",
        ".avi",
        ".flv",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".ts",
        ".webm",
        ".wmv",
    ]);
    public static IReadOnlySet<string> SupportedVideoExtensions => _supportedVideoExtensions;

    private static readonly IReadOnlySet<string> _supportedAudioExtensions = new HashSet<string>(
    [
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
        ".wma",
    ]);
    public static IReadOnlySet<string> SupportedAudioExtensions => _supportedAudioExtensions;

    private static readonly Lazy<IReadOnlySet<string>> _supportedExternalFileExtensions = new(() =>
    {
        HashSet<string> results = [];
        results.UnionWith(SupportedVideoExtensions);
        results.UnionWith(SupportedAudioExtensions);
        return results;
    });
    public static IReadOnlySet<string> SupportedExternalFileExtensions => _supportedExternalFileExtensions.Value;

    public static bool IsSupportedExternalFileExtension(string extension)
    {
        return SupportedExternalFileExtensions.Contains(extension.ToLower());
    }

    public static bool IsSupportedVideoExtension(string extension)
    {
        return SupportedVideoExtensions.Contains(extension.ToLower());
    }

    public static bool IsSupportedAudioExtension(string extension)
    {
        return SupportedAudioExtensions.Contains(extension.ToLower());
    }
}
