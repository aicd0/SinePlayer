// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.ErrorHandling;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Common.Playback;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Data.Models.Playback;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;

namespace SinePlayerUWP.Views.Pages.Player;

internal partial class PlayerPageViewModel : INotifyPropertyChanged
{
    private const long POSITION_SAVE_INTERVAL = 5000;

    private ActionHandler _actionHandler = ActionHandler.Dummy;

    // Media status
    private MediaModel? _media;
    private LoadMediaArgs? _pendingMedia;
    private bool _isLoading = false;
    private bool? _isFavorite = null;
    private bool _isInitialized = false;
    private IMediaEngine? _engine;
    private long _pendingStartPositionMs = -1;
    private long _lastPositionSaveTicks = 0;

    public PlayerPageViewModel() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<string> TitleLiveData = new();
    public readonly MutableLiveData<bool> PlaybackChangeLiveData = new();
    public readonly MutableLiveData<PlayerStatusInfo> PlayerStatusLiveData = new(new() { Status = PlayerStatusEnum.Loading });
    public readonly MutableLiveData<bool> MediaChangedLiveData = new();
    public readonly MutableLiveData<bool> IsExternalMediaLiveData = new(true);
    public readonly MutableLiveData<bool> IsFavoriteLiveData = new();

    private bool _isFullscreen = false;
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            _isFullscreen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFullscreen)));
        }
    }

    private string _fullscreenButtonGlyph = string.Empty;
    public string FullscreenButtonGlyph
    {
        get => _fullscreenButtonGlyph;
        set
        {
            _fullscreenButtonGlyph = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenButtonGlyph)));
        }
    }

    private string _fullscreenButtonText = string.Empty;
    public string FullscreenButtonText
    {
        get => _fullscreenButtonText;
        set
        {
            _fullscreenButtonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullscreenButtonText)));
        }
    }

    private bool _isPinned = false;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }

    private string _pinButtonGlyph = string.Empty;
    public string PinButtonGlyph
    {
        get => _pinButtonGlyph;
        set
        {
            _pinButtonGlyph = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonGlyph)));
        }
    }

    private string _pinButtonText = string.Empty;
    public string PinButtonText
    {
        get => _pinButtonText;
        set
        {
            _pinButtonText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonText)));
        }
    }

    private bool _isAutoPlaying = false;
    public bool IsAutoPlaying
    {
        get => _isAutoPlaying;
        set
        {
            _isAutoPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAutoPlaying)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonGlyph)));
        }
    }

    public string PlayOrPauseButtonTooltip
    {
        get => _isAutoPlaying ? StringResourceProvider.Instance.Pause : StringResourceProvider.Instance.Play;
    }

    public string PlayOrPauseButtonGlyph
    {
        get => _isAutoPlaying ? "\uE769" : "\uE768";
    }

    private bool _isPlaybackNextEnabled = false;
    public bool IsPlaybackNextEnabled
    {
        get => _isPlaybackNextEnabled;
        set
        {
            _isPlaybackNextEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaybackNextEnabled)));
        }
    }

    private bool _isPlaybackPreviousEnabled = false;
    public bool IsPlaybackPreviousEnabled
    {
        get => _isPlaybackPreviousEnabled;
        set
        {
            _isPlaybackPreviousEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaybackPreviousEnabled)));
        }
    }

    private string? _backgroundImageUri = null;
    public string? BackgroundImageUri
    {
        get => _backgroundImageUri;
        set
        {
            _backgroundImageUri = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundImageUri)));
        }
    }

    public PlaylistModel Playlist { get; private set; } = PlaylistModel.CreateEmpty();
    public PlaybackModel Playback { get; } = new();
    public PlayerStatusEnum PlayerStatus => PlayerStatusLiveData.Value.Status;
    public MediaModel? Media => _media;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        Playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
    }

    public void Destory()
    {
        SaveCurrentPosition();
        Playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
        _isInitialized = false;
        _pendingMedia = null;
        _pendingStartPositionMs = -1;

        IMediaEngine? engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            engine.StateChanged -= Engine_StateChanged;
            engine.PositionChanged -= Engine_PositionChanged;
            engine.MediaOpened -= Engine_MediaOpened;
            engine.MediaEnded -= Engine_MediaEnded;
            engine.ErrorOccurred -= Engine_ErrorOccurred;
            engine.Dispose();
        }
    }

    public void LoadPlaylist(PlaylistModel playlist, string? serializedPlayback, long initialPositionMs)
    {
        Playlist = playlist;
        Playback.LoadState(playlist, serializedPlayback, initialPositionMs);
    }

    public UIElement GetVideoSurface()
    {
        return EnsureEngine().CreateVideoSurface();
    }

    public void Pause()
    {
        _engine?.Pause();
        SaveCurrentPosition();
    }

    public void TogglePlayPause()
    {
        IMediaEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        if (engine.State == MediaEngineStateEnum.Playing)
        {
            engine.Pause();
        }
        else
        {
            engine.Play();
        }
    }

    public void SetIsFavorite(bool isFavorite, bool writeDatabase)
    {
        if (_isFavorite == isFavorite)
        {
            return;
        }

        _isFavorite = isFavorite;
        IsFavoriteLiveData.Emit(isFavorite);

        MediaModel? media = _media;
        if (writeDatabase && media != null && !media.IsExternal)
        {
            if (isFavorite)
            {
                FavoriteModel.Instance.Add(media.Id, media.Title1, true);
            }
            else
            {
                FavoriteModel.Instance.RemoveWithId(media.Id, true);
            }
        }
    }

    public void SetFullscreen(bool isFullscreen)
    {
        IsFullscreen = isFullscreen;
        if (isFullscreen)
        {
            FullscreenButtonGlyph = "\uE73F";
            FullscreenButtonText = StringResourceProvider.Instance.ExitFullscreen;
        }
        else
        {
            FullscreenButtonGlyph = "\uE740";
            FullscreenButtonText = StringResourceProvider.Instance.EnterFullscreen;
        }
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;
        if (pinned)
        {
            PinButtonGlyph = "\uE77A";
            PinButtonText = StringResourceProvider.Instance.Unpin;
        }
        else
        {
            PinButtonGlyph = "\uE718";
            PinButtonText = StringResourceProvider.Instance.Pin;
        }
    }

    public void UpdateFavoriteStatus()
    {
        MediaModel? media = _media;
        if (media is not null)
        {
            UpdateFavoriteStatusInternal(media);
        }
    }

    public void UpdateMediaMetadata()
    {
        MediaModel? media = _media;
        if (media is null)
        {
            return;
        }

        BackgroundImageUri = media.GetExt(MediaExt.BACKGROUND_IMAGE);
    }

    public async Task<IReadOnlyList<BaseMenuFlyoutItemModel>> CreateMediaContextMenuItems()
    {
        MediaModel? media = Media;
        if (media is null)
        {
            return [];
        }

        return await MenuFlyoutItemsCreator.CreateMediaMenuItems(
            _actionHandler,
            media,
            playlist: Playlist.ToBuilder(),
            playback: Playback.ToBuilder());
    }

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        IsPlaybackNextEnabled = Playback.CanGoNext;
        IsPlaybackPreviousEnabled = Playback.CanGoPrevious;

        PlaylistModel.PlaylistItem? playlistItem = Playback.CurrentItem;
        if (playlistItem is null)
        {
            TitleLiveData.Emit(StringResourceProvider.Instance.Error);
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Error,
                Description = "The playlist is empty.",
            });
        }
        else if (playlistItem.Media != _media || args.Reason == PlaybackStateChangeReason.Refresh)
        {
            TitleLiveData.Emit(playlistItem.Media.Title);
            PlayerStatusLiveData.Emit(new() { Status = PlayerStatusEnum.Loading });
            LoadMedia(new()
            {
                Media = playlistItem.Media,
                PlaybackArgs = args,
            });
        }

        PlaybackChangeLiveData.Emit(true);
    }

    private void LoadMedia(LoadMediaArgs args)
    {
        CoroutineUtils.Run(async () =>
        {
            if (_isLoading)
            {
                _pendingMedia = args;
                return;
            }

            _isLoading = true;
            try
            {
                LoadMediaArgs? loadingMedia = args;
                while (loadingMedia != null)
                {
                    await LoadMediaInternal(loadingMedia);
                    loadingMedia = _pendingMedia;
                    _pendingMedia = null;
                }
            }
            finally
            {
                _isLoading = false;
            }

            MediaChangedLiveData.Emit(true);
        });
    }

    private async Task LoadMediaInternal(LoadMediaArgs args)
    {
        // Save the position of the previous media
        SaveCurrentPosition();

        MediaModel? media = args.Media;
        _media = null;
        BackgroundImageUri = null;

        if (media is null)
        {
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Error,
                Description = "No media is selected.",
            });
            return;
        }

        _media = media;
        UpdateMediaMetadata();

        // Save to history
        if (!media.IsExternal)
        {
            await media.SetAsVisited();
            if (AppSettingsModel.SaveBrowsingHistory)
            {
                await MediaHistoryItemModel.AddAsync(media.Id, media.Title1);
            }
        }

        IsExternalMediaLiveData.Emit(media.IsExternal);
        UpdateFavoriteStatusInternal(media);

        _pendingStartPositionMs = ResolveStartPositionMs(media, args.PlaybackArgs);
        PlayerStatusLiveData.Emit(new() { Status = PlayerStatusEnum.Loading });

        IMediaEngine engine = EnsureEngine();
        ErrorResult err = await engine.LoadAsync(media.Location);
        if (!err.IsSuccessful)
        {
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Error,
                Description = err.DetailedErrorMessage,
            });
        }
    }

    private void SaveCurrentPosition()
    {
        MediaModel? media = _media;
        IMediaEngine? engine = _engine;
        if (media is null || engine is null || media.IsExternal)
        {
            return;
        }

        long positionMs = engine.Position.Ticks / TimeSpan.TicksPerMillisecond;
        long durationMs = engine.Duration.Ticks / TimeSpan.TicksPerMillisecond;
        int progress = durationMs > 0 ? (int)Math.Clamp(positionMs * 100 / durationMs, 0, 100) : -1;
        CoroutineUtils.Run(async () => await media.SetProgress(progress, positionMs));
    }

    private static long ResolveStartPositionMs(MediaModel media, PlaybackStateChangedEventArgs args)
    {
        long overridePositionMs = args.InitialPositionMs;
        if (overridePositionMs >= 0)
        {
            return overridePositionMs;
        }

        switch (args.Reason)
        {
            case PlaybackStateChangeReason.Next:
            case PlaybackStateChangeReason.Previous:
            case PlaybackStateChangeReason.PreviousByOverScroll:
                return 0;

            default:
                {
                    bool restorePosition =
                        AppSettingsModel.RestoreLastReadingPosition &&
                        !(AppSettingsModel.RestoreLastReadingPositionOnlyAppliesToReadingMedia && media.CompletionStatus != CompletionStatusEnum.Reading);
                    return restorePosition ? media.LastPositionMs : 0;
                }
        }
    }

    private IMediaEngine EnsureEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        IMediaEngine engine = MediaEngineService.CreateEngine();
        engine.StateChanged += Engine_StateChanged;
        engine.PositionChanged += Engine_PositionChanged;
        engine.MediaOpened += Engine_MediaOpened;
        engine.MediaEnded += Engine_MediaEnded;
        engine.ErrorOccurred += Engine_ErrorOccurred;
        _engine = engine;
        return engine;
    }

    private void Engine_StateChanged(MediaEngineStateEnum state)
    {
        switch (state)
        {
            case MediaEngineStateEnum.Playing:
                IsAutoPlaying = true;
                break;
            case MediaEngineStateEnum.None:
            case MediaEngineStateEnum.Paused:
            case MediaEngineStateEnum.Ended:
            case MediaEngineStateEnum.Error:
                IsAutoPlaying = false;
                break;
            default:
                break;
        }
    }

    private void Engine_PositionChanged()
    {
        long now = Environment.TickCount64;
        if (now - _lastPositionSaveTicks < POSITION_SAVE_INTERVAL)
        {
            return;
        }

        _lastPositionSaveTicks = now;
        SaveCurrentPosition();
    }

    private void Engine_MediaOpened()
    {
        MediaModel? media = _media;
        IMediaEngine? engine = _engine;
        if (media is null || engine is null)
        {
            return;
        }

        // Persist the duration probed by the engine when it is still unknown
        long durationMs = engine.Duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (durationMs > 0 && media.DurationMs <= 0 && !media.IsExternal)
        {
            CoroutineUtils.Run(async () => await media.SetDuration(durationMs));
        }

        long startPositionMs = _pendingStartPositionMs;
        _pendingStartPositionMs = -1;
        if (startPositionMs > 0)
        {
            engine.Seek(TimeSpan.FromMilliseconds(startPositionMs));
        }

        PlayerStatusLiveData.Emit(new() { Status = PlayerStatusEnum.Working });
        engine.Play();
    }

    private void Engine_MediaEnded()
    {
        MediaModel? media = _media;
        if (media is not null && !media.IsExternal)
        {
            CoroutineUtils.Run(async () => await media.SetProgress(100, 0));
        }

        Playback.Next();
    }

    private void Engine_ErrorOccurred(string message)
    {
        PlayerStatusLiveData.Emit(new()
        {
            Status = PlayerStatusEnum.Error,
            Description = message,
        });
    }

    private void UpdateFavoriteStatusInternal(MediaModel media)
    {
        bool isFavorite = !media.IsExternal && FavoriteModel.Instance.FromId(media.Id) != null;
        SetIsFavorite(isFavorite, false);
    }

    //
    // Types
    //

    public enum PlayerStatusEnum
    {
        Loading,
        Error,
        Working,
    }

    public class PlayerStatusInfo
    {
        public required PlayerStatusEnum Status { get; init; }
        public string Description { get; init; } = string.Empty;
        public int Progress { get; init; } = -1;
    }

    private class LoadMediaArgs
    {
        public required MediaModel? Media { get; init; }
        public required PlaybackStateChangedEventArgs PlaybackArgs { get; init; }
    }
}
