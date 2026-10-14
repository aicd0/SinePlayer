// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using SinePlayer.Common.BaseUI;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Views.Pages.Collection;
using SinePlayer.Views.Pages.DevTools;
using SinePlayer.Views.Pages.Home;
using SinePlayer.Views.Pages.Main;
using SinePlayer.Views.Pages.PlaybackLab;
using SinePlayer.Views.Pages.Search;
using SinePlayer.Views.Pages.Sidebar.Favorite;
using SinePlayer.Views.Pages.Sidebar.FilterPresets;
using SinePlayer.Views.Pages.Sidebar.Folders;
using SinePlayer.Views.Pages.Sidebar.History;
using SinePlayer.Views.Pages.Sidebar.MediaInfo;
using SinePlayer.Views.Pages.Sidebar.PlayQueue;
using SinePlayer.Views.Pages.Sidebar.Tags;

namespace SinePlayer.Helpers.Navigation;

internal class OpenPageInterceptor : IRouterInterceptor
{
    private readonly Dictionary<string, IPageTrait> _internalHosts = new()
    {
        { RouterConstants.HOST_MAIN, new DefaultPageTrait(typeof(MainPage)) },
        { RouterConstants.HOST_PLAYER, PlayerPageTrait.Instance },
        { RouterConstants.HOST_HOME, new DefaultPageTrait(typeof(HomePage)) },
        { RouterConstants.HOST_SEARCH, new DefaultPageTrait(typeof(SearchPage)) },
        { RouterConstants.HOST_COLLECTION, new DefaultPageTrait(typeof(CollectionPage)) },
        { RouterConstants.HOST_SETTINGS, SettingsPageTrait.Instance },
        { RouterConstants.HOST_SIDE_PANE_FAVORITE, new DefaultPageTrait(typeof(FavoritePage)) },
        { RouterConstants.HOST_SIDE_PANE_HISTORY, new DefaultPageTrait(typeof(HistoryPage)) },
        { RouterConstants.HOST_SIDE_PANE_TAGS, new DefaultPageTrait(typeof(TagsPage)) },
        { RouterConstants.HOST_SIDE_PANE_FOLDERS, new DefaultPageTrait(typeof(FoldersPage)) },
        { RouterConstants.HOST_SIDE_PANE_FILTER_PRESETS, new DefaultPageTrait(typeof(FilterPresetsPage)) },
        { RouterConstants.HOST_SIDE_PANE_PLAY_QUEUE, new DefaultPageTrait(typeof(PlayQueuePage)) },
        { RouterConstants.HOST_SIDE_PANE_MEDIA_INFO, new DefaultPageTrait(typeof(MediaInfoPage)) },
        { RouterConstants.HOST_DEV_TOOLS, new DefaultPageTrait(typeof(DevToolsPage)) },
        { RouterConstants.HOST_PLAYBACK_LAB, new DefaultPageTrait(typeof(PlaybackLabPage)) },
    };

    private readonly ConcurrentDictionary<string, IPageTrait> _hosts = [];

    public OpenPageInterceptor()
    {
        foreach (KeyValuePair<string, IPageTrait> item in _internalHosts)
        {
            _hosts[item.Key] = item.Value;
        }
    }

    public bool Intercept(Route route, out PageNavigationBundle? navigationBundle)
    {
        string host = route.Host;
        if (!_hosts.TryGetValue(host, out IPageTrait? pageTrait))
        {
            Logger.F(nameof(OpenPageInterceptor), $"Unknown host: '{host}'");
            navigationBundle = null;
            return false;
        }

        if ((host == RouterConstants.HOST_DEV_TOOLS || host == RouterConstants.HOST_PLAYBACK_LAB) && !DebugUtils.DeveloperMode)
        {
            navigationBundle = null;
            return false;
        }

        navigationBundle = new PageNavigationBundle(route)
        {
            PageTrait = pageTrait,
        };
        return true;
    }

    public void RegisterPage(string host, IPageTrait pageTrait)
    {
        if (!_hosts.TryAdd(host, pageTrait))
        {
            throw new ArgumentException($"Host '{host}' is already presented.");
        }
    }
}
