// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Storage;

namespace SinePlayerUWP.Data.Models.Media;

internal static class MediaExt
{
    public const string BACKGROUND_IMAGE = "BackgroundImage";
    public const string COVER_IMAGE = "CoverImage";
    public const string COVER_INDEX = "CoverIndex";
    public const string LINKS = "Links";
    public const string RESOURCE_UUID = "ResourceUuid";

    public static int GetCoverIndex(MediaModel media)
    {
        if (media.IsCollection)
        {
            return -1;
        }

        string? coverIndexString = media.GetExt(COVER_INDEX);
        if (!string.IsNullOrEmpty(coverIndexString) && int.TryParse(coverIndexString, out int coverIndex) && coverIndex >= 0)
        {
            return coverIndex;
        }

        string? coverImage = media.GetExt(COVER_IMAGE);
        if (string.IsNullOrEmpty(coverImage))
        {
            return 0;
        }

        return -1;
    }

    public static string? GetCoverImageUri(MediaModel media)
    {
        int coverIndex = GetCoverIndex(media);
        if (coverIndex < 0)
        {
            return media.GetExt(COVER_IMAGE);
        }

        return media.IsExternal ? null : ResourceUri.CreateMediaImage(media.Id, coverIndex).ToString();
    }
}
