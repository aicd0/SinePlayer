// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Storage;

namespace SinePlayer.Data.Models.Media;

internal static class MediaExt
{
    public const string BACKGROUND_IMAGE = "BackgroundImage";
    public const string COVER_IMAGE = "CoverImage";
    public const string CUSTOM_PLAYER_SETTINGS = "CustomPlayerSettings";
    public const string LINKS = "Links";
    public const string PLAYER_SETTING_PRESET_KEY = "PlayerSettingPresetKey";
    public const string RESOURCE_UUID = "ResourceUuid";
    public const string SELECTED_AUDIO_TRACK = "SelectedAudioTrack";
    public const string SELECTED_SUBTITLE_TRACK = "SelectedSubtitleTrack";
    public const string SELECTED_VIDEO_TRACK = "SelectedVideoTrack";

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
