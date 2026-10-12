// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

using SinePlayer.Common.Actions;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Helpers.MenuFlyoutHelpers;

namespace SinePlayer.Views.Pages.Sidebar.Playlist;

internal partial class PlaylistPageViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _noMediaVisible = false;
    public bool NoMediaVisible
    {
        get => _noMediaVisible;
        set
        {
            if (_noMediaVisible != value)
            {
                _noMediaVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoMediaVisible)));
            }
        }
    }

    private int _selectedIndex = -1;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
        }
    }

    public ObservableCollection<PlaylistItemViewModel> DataSource { get; set; } = [];

    public MutableLiveData<PlaylistItemViewModel> ScrollToItemLiveData = new();

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private PlaybackModel _playback = new();

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        UpdatePlaylist();
    }

    public void Destory()
    {
        _playback.PlaylistChanged -= Playback_PlaylistChanged;
        _playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
    }

    public void SetPlayback(PlaybackModel playback)
    {
        _playback.PlaylistChanged -= Playback_PlaylistChanged;
        _playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
        _playback = playback;
        _playback.PlaylistChanged += Playback_PlaylistChanged;
        _playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
        UpdatePlaylist();
        UpdatePlaybackState();
    }

    public void SetSelectedIndex(int index)
    {
        if (_selectedIndex == index)
        {
            return;
        }

        _selectedIndex = index;
        _playback.Cursor = index;
    }

    public void UpdateMedia()
    {
        UpdatePlaylist();
    }

    private void Playback_PlaylistChanged()
    {
        UpdatePlaylist();
    }

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        UpdatePlaybackState();
    }

    private void UpdatePlaylist()
    {
        List<PlaylistItemViewModel> newPlaylist = [];
        PlaylistModel.Builder playlist = new PlaylistModel.Builder().AddItems(_playback.Items);
        foreach (PlaylistModel.PlaylistItem playlistItem in _playback.Items)
        {
            newPlaylist.Add(new()
            {
                Media = playlistItem.Media,
                Title = playlistItem.Media.Title,
                Progress = GetProgressText(playlistItem.Media),
                RequestContextMenuItemsAsync = item =>
                {
                    return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                        _actionHandler,
                        playlistItem.Media,
                        playlist: playlist,
                        playback: _playback.ToBuilder());
                },
            });
        }

        CoroutineUtils.RunInMainThread(() =>
        {
            DiffUtils.UpdateCollection(DataSource, newPlaylist, (a, b) => a.Media == b.Media, (a, b) =>
            {
                a.Title = b.Title;
                a.Progress = b.Progress;
                a.RequestContextMenuItemsAsync = b.RequestContextMenuItemsAsync;
            });
            NoMediaVisible = DataSource.Count == 0;
        });
    }

    private void UpdatePlaybackState()
    {
        CoroutineUtils.RunInMainThread(() =>
        {
            int cursor = _playback.Cursor;
            if (cursor >= 0 && cursor < DataSource.Count)
            {
                SelectedIndex = cursor;
                ScrollToItemLiveData.Emit(DataSource[cursor]);
            }
        });
    }

    private static string GetProgressText(MediaModel media)
    {
        CompletionStatusEnum status = media.CompletionStatus;

        if (CompletionStatusService.CanTransitToOngoingAutomatically(status))
        {
            return string.Empty;
        }

        return status switch
        {
            CompletionStatusEnum.Ongoing => Math.Clamp(media.Progress, 0, 100).ToString() + "%",
            _ => CompletionStatusService.EnumToString(status),
        };
    }
}
