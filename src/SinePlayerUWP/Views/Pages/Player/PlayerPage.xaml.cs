// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.BaseUI.PageAbilities;
using SinePlayerUWP.Common.Constants;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Database;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Helpers.Navigation;

namespace SinePlayerUWP.Views.Pages.Player;

internal sealed partial class PlayerPage : BasePage
{
    // Can only be accessed by UI thread
    public static IReadOnlyList<Tuple<int, string>> ActiveTabs { get; private set; } = [];

    private readonly PlayerPageViewModel ViewModel = new();

    private string _loadedKey = string.Empty;

    //
    // Constructor
    //

    public PlayerPage()
    {
        InitializeComponent();
    }

    //
    // Lifecycle
    //

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);
        AddToActiveTabs();
        GetMainPageAbility().SetIcon(new SymbolIconSource { Symbol = Symbol.Play });

        ViewModel.Initialize(PageActionHandler);
        ObserveData();

        string playlistId = bundle.GetString(RouterConstants.ARG_PLAYLIST_ID) ?? string.Empty;
        string serializedPlayback = bundle.GetString(RouterConstants.ARG_PLAYBACK) ?? string.Empty;
        long initialPositionMs = -1;
        string? positionString = bundle.GetString(RouterConstants.ARG_POSITION);
        if (!string.IsNullOrEmpty(positionString))
        {
            long.TryParse(positionString, CultureInfo.InvariantCulture, out initialPositionMs);
        }

        string loadKey = $"{playlistId}|{serializedPlayback}|{initialPositionMs}";
        if (loadKey != _loadedKey)
        {
            _loadedKey = loadKey;
            CoroutineUtils.Run(async () => await LoadPlaylistAsync(playlistId, serializedPlayback, initialPositionMs));
        }

        VideoHost.Children.Clear();
        VideoHost.Children.Add(ViewModel.GetVideoSurface());
    }

    protected override void OnResume()
    {
        base.OnResume();
        AddToActiveTabs();
    }

    protected override void OnPause()
    {
        base.OnPause();
        ViewModel.Pause();
    }

    protected override void OnStop()
    {
        base.OnStop();
        ViewModel.Destory();
        _loadedKey = string.Empty;
        RemoveFromActiveTabs();
    }

    //
    // Load
    //

    private void ObserveData()
    {
        ViewModel.TitleLiveData.Observe(this, title =>
        {
            GetMainPageAbility().SetTitle(title);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.PlayerStatusLiveData.Observe(this, status =>
        {
            switch (status.Status)
            {
                case PlayerPageViewModel.PlayerStatusEnum.Working:
                    BusyRing.IsActive = false;
                    StatusPanel.Visibility = Visibility.Collapsed;
                    StatusText.Text = string.Empty;
                    break;
                case PlayerPageViewModel.PlayerStatusEnum.Loading:
                    BusyRing.IsActive = true;
                    StatusPanel.Visibility = Visibility.Visible;
                    StatusText.Text = status.Description;
                    break;
                case PlayerPageViewModel.PlayerStatusEnum.Error:
                    BusyRing.IsActive = false;
                    StatusPanel.Visibility = Visibility.Visible;
                    StatusText.Text = status.Description;
                    break;
                default:
                    break;
            }
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });
    }

    private async Task LoadPlaylistAsync(string playlistId, string serializedPlayback, long initialPositionMs)
    {
        if (string.IsNullOrEmpty(playlistId))
        {
            return;
        }

        string? serializedPlaylist = AppDB.MainRegistry.CreateKey(RegistryNames.PLAYLISTS).GetValue<string>(playlistId);
        if (string.IsNullOrEmpty(serializedPlaylist))
        {
            return;
        }

        PlaylistModel? playlist = await PlaylistModel.CreateFromSerializedString(serializedPlaylist);
        if (playlist is null)
        {
            return;
        }

        ViewModel.LoadPlaylist(playlist, serializedPlayback, initialPositionMs);
    }

    //
    // Events
    //

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.TogglePlayPause();
    }

    //
    // Utilities
    //

    private IMainPageAbilityForTab GetMainPageAbility()
    {
        return GetAbility<IMainPageAbilityForTab>()!;
    }

    private void AddToActiveTabs()
    {
        if (!IsStarted)
        {
            return;
        }

        int windowId = WindowId;
        string tabId = GetMainPageAbility().TabId;

        if (ActiveTabs.Count > 0)
        {
            Tuple<int, string> tab = ActiveTabs[ActiveTabs.Count - 1];
            if (tab.Item1 == windowId && tab.Item2 == tabId)
            {
                return;
            }
        }

        List<Tuple<int, string>> copy = [.. ActiveTabs];
        for (int i = copy.Count - 1; i >= 0; i--)
        {
            Tuple<int, string> tab = copy[i];
            if (tab.Item1 == windowId && tab.Item2 == tabId)
            {
                copy.RemoveAt(i);
            }
        }

        copy.Add(new(windowId, tabId));
        ActiveTabs = copy;
    }

    private void RemoveFromActiveTabs()
    {
        int windowId = WindowId;
        string tabId = GetMainPageAbility().TabId;

        List<Tuple<int, string>> copy = [.. ActiveTabs];
        for (int i = copy.Count - 1; i >= 0; i--)
        {
            Tuple<int, string> tab = copy[i];
            if (tab.Item1 == windowId && tab.Item2 == tabId)
            {
                copy.RemoveAt(i);
            }
        }

        ActiveTabs = copy;
    }
}
