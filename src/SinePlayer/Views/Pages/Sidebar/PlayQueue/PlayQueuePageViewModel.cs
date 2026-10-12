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

namespace SinePlayer.Views.Pages.Sidebar.PlayQueue;

internal partial class PlayQueuePageViewModel : INotifyPropertyChanged
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

    public ObservableCollection<PlayQueueItemViewModel> DataSource { get; set; } = [];

    public MutableLiveData<PlayQueueItemViewModel> ScrollToItemLiveData = new();

    private ActionHandler _actionHandler = ActionHandler.Dummy;
    private PlaybackModel _playback = new();

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        UpdatePlayQueue();
    }

    public void Destory()
    {
        _playback.PlayQueueChanged -= Playback_PlayQueueChanged;
        _playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
    }

    public void SetPlayback(PlaybackModel playback)
    {
        _playback.PlayQueueChanged -= Playback_PlayQueueChanged;
        _playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
        _playback = playback;
        _playback.PlayQueueChanged += Playback_PlayQueueChanged;
        _playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
        UpdatePlayQueue();
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
        UpdatePlayQueue();
    }

    private void Playback_PlayQueueChanged()
    {
        UpdatePlayQueue();
    }

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        UpdatePlaybackState();
    }

    private void UpdatePlayQueue()
    {
        List<PlayQueueItemViewModel> newPlayQueue = [];
        PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddItems(_playback.Items);
        foreach (PlayQueueModel.PlayQueueItem playQueueItem in _playback.Items)
        {
            newPlayQueue.Add(new()
            {
                Media = playQueueItem.Media,
                Title = playQueueItem.Media.Title,
                Progress = GetProgressText(playQueueItem.Media),
                RequestContextMenuItemsAsync = item =>
                {
                    return MenuFlyoutItemsCreator.CreateMediaMenuItems(
                        _actionHandler,
                        playQueueItem.Media,
                        playQueue: playQueue,
                        playback: _playback.ToBuilder());
                },
            });
        }

        CoroutineUtils.RunInMainThread(() =>
        {
            DiffUtils.UpdateCollection(DataSource, newPlayQueue, (a, b) => a.Media == b.Media, (a, b) =>
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
