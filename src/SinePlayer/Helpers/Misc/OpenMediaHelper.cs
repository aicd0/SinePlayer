// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

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
using SinePlayer.Views.AppWindows.Main;
using SinePlayer.Views.Pages.Player;

namespace SinePlayer.Helpers.Misc;

internal static class OpenMediaHelper
{
    private const string TAG = nameof(OpenMediaHelper);

    public static Route GetMediaRoute(
        MediaModel media,
        long positionMs = -1,
        PlayQueueModel.Builder? playQueue = null,
        PlaybackModel.Builder? playback = null)
    {
        if (media.IsCollection)
        {
            return Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_COLLECTION)
                .WithParam(RouterConstants.ARG_ID, media.Id.ToString());
        }

        playQueue ??= new PlayQueueModel.Builder();
        string itemId = playQueue.EnsureMedia(media);

        playback ??= new PlaybackModel.Builder();
        playback.CurrentId = itemId;

        string playQueueId = Guid.NewGuid().ToString();
        AppDB.MainRegistry.CreateKey(RegistryNames.PLAY_QUEUES).Set(playQueueId, playQueue.ToSerializedString());

        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_PLAYER)
            .WithParam(RouterConstants.ARG_PLAY_QUEUE_ID, playQueueId)
            .WithParam(RouterConstants.ARG_PLAYBACK, playback.ToSerializedString());

        if (positionMs >= 0)
        {
            route = route.WithParam(RouterConstants.ARG_POSITION, positionMs.ToString(CultureInfo.InvariantCulture));
        }

        return route;
    }

    public static void OpenMedia(ActionHandler actionHandler, Route route)
    {
        OpenTarget target = ResolveTarget();

        ActionModel.Builder builder = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, route.Url);

        if (target.WindowId is int windowId)
        {
            builder = builder.AddParameter(OpenTabProvider.PARAM_WINDOW_ID, windowId);
        }

        if (target.TabId is string tabId)
        {
            builder = builder.AddParameter(OpenTabProvider.PARAM_TAB_ID, tabId);
        }

        actionHandler.HandleNoResult(builder.Build());
    }

    public static MainWindow OpenMedia(MainWindow window, Route route)
    {
        OpenTarget target = ResolveTarget();
        MainWindow targetWindow = target.WindowId is int windowId && App.Instance.WindowManager.GetWindow(windowId) is MainWindow other ? other : window;
        targetWindow.OpenTab(route.Url, OpenTabProvider.ResolveTabId(targetWindow, target.TabId), string.Empty);
        return targetWindow;
    }

    private static OpenTarget ResolveTarget()
    {
        switch (AppSettingsModel.OpenItemDefaultBehavior)
        {
            case AppSettingsModel.OpenItemBehaviorEnum.OpenInCurrentTab:
                return new(null, null);
            case AppSettingsModel.OpenItemBehaviorEnum.OpenInNewTab:
                return new(null, OpenTabProvider.TAB_ID_NEW);
            case AppSettingsModel.OpenItemBehaviorEnum.OpenInLastActivePlayerTab:
                {
                    IReadOnlyList<Tuple<int, string>> activePlayerTabs = PlayerPage.ActiveTabs;
                    return activePlayerTabs.Count == 0
                        ? new(null, OpenTabProvider.TAB_ID_NEW)
                        : new(activePlayerTabs[activePlayerTabs.Count - 1].Item1, activePlayerTabs[activePlayerTabs.Count - 1].Item2);
                }
            default:
                Logger.F(TAG, $"Unknown enum value {AppSettingsModel.OpenItemDefaultBehavior}");
                return new(null, null);
        }
    }

    private readonly record struct OpenTarget(int? WindowId, string? TabId);
}
