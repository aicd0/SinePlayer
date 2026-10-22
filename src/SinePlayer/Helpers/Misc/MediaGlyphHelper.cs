// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Tables;

namespace SinePlayer.Helpers.Misc;

internal static class MediaGlyphHelper
{
    public const string AudioGlyph = "\uE8D6";
    public const string VideoGlyph = "\uE714";
    public const string MediaGlyph = "\uEA69";

    public static string GetMediaGlyph(MediaModel? media)
    {
        return media?.Type switch
        {
            MediaType.Audio => AudioGlyph,
            MediaType.Video => VideoGlyph,
            _ => MediaGlyph,
        };
    }
}
