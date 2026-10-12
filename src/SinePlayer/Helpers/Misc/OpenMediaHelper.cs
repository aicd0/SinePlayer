// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.Constants;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Views.Pages.Player;

namespace SinePlayer.Helpers.Misc;

internal static class OpenMediaHelper
{
    private const string TAG = nameof(OpenMediaHelper);

    public static Route GetMediaRoute(
        MediaModel media,
        long positionMs = -1,
        PlaylistModel.Builder? playlist = null,
        PlaybackModel.Builder? playback = null)
    {
        if (media.IsCollection)
        {
            return Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_COLLECTION)
                .WithParam(RouterConstants.ARG_ID, media.Id.ToString());
        }

        playlist ??= new PlaylistModel.Builder();
        string itemId = playlist.EnsureMedia(media);

        playback ??= new PlaybackModel.Builder();
        playback.CurrentId = itemId;

        string playlistId = Guid.NewGuid().ToString();
        AppDB.MainRegistry.CreateKey(RegistryNames.PLAYLISTS).Set(playlistId, playlist.ToSerializedString());

        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_PLAYER)
            .WithParam(RouterConstants.ARG_PLAYLIST_ID, playlistId)
            .WithParam(RouterConstants.ARG_PLAYBACK, playback.ToSerializedString());

        if (positionMs >= 0)
        {
            route = route.WithParam(RouterConstants.ARG_POSITION, positionMs.ToString(CultureInfo.InvariantCulture));
        }

        return route;
    }

    public static void OpenMedia(ActionHandler actionHandler, Route route)
    {
        AppSettingsModel.OpenMediaBehaviorEnum behavior = AppSettingsModel.OpenMediaDefaultBehavior;
        switch (behavior)
        {
            case AppSettingsModel.OpenMediaBehaviorEnum.OpenInCurrentTab:
                {
                    ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                        .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                }
                break;
            case AppSettingsModel.OpenMediaBehaviorEnum.OpenInNewTab:
                {
                    ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                        .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                        .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                }
                break;
            case AppSettingsModel.OpenMediaBehaviorEnum.OpenInLastActiveReaderTab:
                {
                    IReadOnlyList<Tuple<int, string>> activePlayerTabs = PlayerPage.ActiveTabs;
                    if (activePlayerTabs.Count == 0)
                    {
                        goto case AppSettingsModel.OpenMediaBehaviorEnum.OpenInNewTab;
                    }

                    int windowId = activePlayerTabs[activePlayerTabs.Count - 1].Item1;
                    string tabId = activePlayerTabs[activePlayerTabs.Count - 1].Item2;
                    ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                        .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                        .AddParameter(OpenTabProvider.PARAM_WINDOW_ID, windowId.ToString())
                        .AddParameter(OpenTabProvider.PARAM_TAB_ID, tabId)
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                }
                break;
            default:
                Logger.F(TAG, $"Unknown enum value {behavior}");
                goto case AppSettingsModel.OpenMediaBehaviorEnum.OpenInCurrentTab;
        }
    }
}
