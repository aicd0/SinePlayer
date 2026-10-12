// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Storage;

namespace SinePlayerUWP.Data.Models.Media;

internal static class MediaExt
{
    public const string BACKGROUND_IMAGE = "BackgroundImage";
    public const string COVER_IMAGE = "CoverImage";
    public const string CUSTOM_PLAYER_SETTINGS = "CustomPlayerSettings";
    public const string LINKS = "Links";
    public const string PLAYER_SETTING_PRESET_KEY = "PlayerSettingPresetKey";
    public const string RESOURCE_UUID = "ResourceUuid";

    public static string? GetCoverImageUri(MediaModel media)
    {
        string? coverImage = media.GetExt(COVER_IMAGE);

        if (media.IsCollection || !string.IsNullOrEmpty(coverImage))
        {
            return coverImage;
        }

        return media.IsExternal ? null : ResourceUri.CreateMediaThumbnail(media.Id).ToString();
    }
}
