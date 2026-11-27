// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Test;

public static class SubtitleFileNameHelper
{
    /// <summary>
    /// Whether a subtitle file belongs to a media file: the parts of their names before the first '.', compared
    /// case-insensitively. That is what lets <c>Episode.sc.ass</c> be recognised as the subtitles of
    /// <c>Episode.mkv</c>, while language suffixes and other dots in a name stay out of the comparison.
    /// </summary>
    public static bool HasSameSubtitleBaseName(string subtitlePath, string mediaPath)
    {
        string subtitleBaseName = GetBaseName(subtitlePath);
        return subtitleBaseName.Length > 0 && string.Equals(subtitleBaseName, GetBaseName(mediaPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBaseName(string path)
    {
        string name = Path.GetFileName(path);
        int index = name.IndexOf('.');
        return index >= 0 ? name[..index] : name;
    }
}
