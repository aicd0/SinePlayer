// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.ComponentModel;
using System.Threading.Tasks;

using SinePlayer.Common.Actions;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Views.Pages.Player;

internal partial class PlayerPageViewModel : INotifyPropertyChanged
{
    #region Constructor

    private const long ALMOST_ENDED_THRESHOLD_MS = 5000;
    private const long POSITION_SAVE_INTERVAL = 5000;
    private const long POSITION_UI_INTERVAL = 200;
    private const int VOLUME_SAVE_DELAY_MS = 500;

    private ActionHandler _actionHandler = ActionHandler.Dummy;

    private MediaModel? _media;
    private bool? _isFavorite = null;
    private long _positionMs;
    private long _durationMs;
    private long _lastPositionUiTicks;
    private long _lastPositionSaveTicks;
    private bool _volumeSaveScheduled;

    public PlayerPageViewModel() { }

    #endregion

    #region Bindings

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<bool> PlaybackChangeLiveData = new();
    public readonly MutableLiveData<PlayerStatusInfo> PlayerStatusLiveData = new(new() { Status = PlayerStatusEnum.Loading });
    public readonly MutableLiveData<bool> IsExternalMediaLiveData = new(true);
    public readonly MutableLiveData<bool> IsFavoriteLiveData = new();
    public readonly MutableLiveData<bool> IsPlayingLiveData = new();
    public readonly MutableLiveData<long> PositionLiveData = new();
    public readonly MutableLiveData<int> VolumeLiveData = new();
    public readonly MutableLiveData<bool> MuteLiveData = new();

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

    private bool _isPlaying = false;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayOrPauseButtonGlyph)));
        }
    }

    public string PlayOrPauseButtonTooltip
    {
        get => _isPlaying ? StringResourceProvider.Instance.Pause : StringResourceProvider.Instance.Play;
    }

    public string PlayOrPauseButtonGlyph
    {
        get => _isPlaying ? "\uE769" : "\uE768";
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

    #endregion

    #region Public API

    public PlayQueueModel PlayQueue { get; private set; } = PlayQueueModel.CreateEmpty();
    public PlaybackModel Playback { get; } = new();
    public PlayerStatusEnum PlayerStatus => PlayerStatusLiveData.Value.Status;
    public MediaModel? Media => _media;
    public long DurationMs { get; set; } = 0;
    public bool IsLoading => _isLoading;

    public void Initialize(ActionHandler actionHandler, OpenMediaHandler openHandler)
    {
        _actionHandler = actionHandler;
        _openHandler = openHandler;
        Playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
    }

    public void Destory()
    {
        Playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
        _media = null;
    }

    public void LoadPlayQueue(PlayQueueModel playQueue, string? serializedPlayback, long initialPositionMs)
    {
        PlayQueue = playQueue;
        Playback.LoadState(playQueue, serializedPlayback, initialPositionMs);
    }

    public void Next()
    {
        Playback.Next();
    }

    public void Previous()
    {
        Playback.Previous(false);
    }

    /// <summary>The duration the engine probed, kept on the media when it was not known before.</summary>
    public void SetDuration(long durationMs)
    {
        _positionMs = 0;
        _durationMs = durationMs;

        MediaModel? media = _media;
        if (durationMs > 0 && media is not null && media.DurationMs <= 0 && !media.IsExternal)
        {
            CoroutineUtils.Run(async () => await media.SetDuration(durationMs));
        }

        DurationMs = durationMs;
    }

    /// <summary>
    /// Where playback has got to, as the engine reports it. The view keeps a position it can show and the media
    /// keeps a mark every few seconds; both cadences and the write are here, so the page only reads the engine.
    /// </summary>
    public void SetPosition(long positionMs, long durationMs)
    {
        _positionMs = positionMs;
        _durationMs = durationMs;

        long now = Environment.TickCount64;
        if (now - _lastPositionUiTicks >= POSITION_UI_INTERVAL)
        {
            _lastPositionUiTicks = now;
            PositionLiveData.Emit(positionMs);
        }

        if (now - _lastPositionSaveTicks < POSITION_SAVE_INTERVAL)
        {
            return;
        }

        SavePosition();
    }

    /// <summary>The level the engine was set to, kept where the player remembers it.</summary>
    public void SetVolume(int volume)
    {
        VolumeLiveData.Emit(Math.Clamp(volume, 0, 100));
        SaveVolumeAndMute();
    }

    /// <summary>Whether the sound is muted, kept where the player remembers it.</summary>
    public void SetMute(bool isMuted)
    {
        MuteLiveData.Emit(isMuted);
        SaveVolumeAndMute();
    }

    public void CycleRepeatMode()
    {
        Playback.RepeatMode = Playback.RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
    }

    public void ToggleShuffle()
    {
        Playback.IsShuffle = !Playback.IsShuffle;
    }

    public void SetFavorite(bool isFavorite, bool writeDatabase)
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

    /// <summary>
    /// Keeps where playback was left on the media it was left in. Reads the engine's last reported position, so a
    /// caller that is about to replace the media saves first, and one that has just paused saves as it is.
    /// </summary>
    public void SavePosition()
    {
        _lastPositionSaveTicks = Environment.TickCount64;

        MediaModel? media = _media;
        if (media is null || media.IsExternal)
        {
            return;
        }

        int progress = _durationMs > 0 ? (int)Math.Clamp(_positionMs * 100 / _durationMs, 0, 100) : -1;
        CoroutineUtils.Run(async () => await media.SetProgress(progress, _positionMs));
    }

    #endregion

    #region Load

    /// <summary>Opens a file, wherever the engine that does it lives. The page supplies this; the view model owns
    /// the rest of loading — which file, where in it, and what the status says while it happens.</summary>
    public delegate Task<ErrorResult> OpenMediaHandler(MediaModel media, long startPositionMs);
    private OpenMediaHandler? _openHandler;

    private LoadMediaArgs? _pendingMedia;
    private bool _isLoading;

    private void Playback_PlaybackStateChanged(PlaybackStateChangedEventArgs args)
    {
        IsPlaybackNextEnabled = Playback.CanGoNext;
        IsPlaybackPreviousEnabled = Playback.CanGoPrevious;

        PlayQueueModel.PlayQueueItem? playQueueItem = Playback.CurrentItem;
        if (playQueueItem is null)
        {
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Error,
                Description = "The play queue is empty.",
            });
        }
        else if (playQueueItem.Media != _media || args.Reason == PlaybackStateChangeReason.Refresh)
        {
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Loading,
            });

            LoadMedia(new()
            {
                Media = playQueueItem.Media,
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
        });
    }

    private async Task LoadMediaInternal(LoadMediaArgs args)
    {
        MediaModel? media = args.Media;
        PositionLiveData.Emit(0);

        await SetMedia(media);
        if (media is null)
        {
            return;
        }

        long startPositionMs = ResolveStartPositionMs(media, args.PlaybackArgs);
        PlayerStatusLiveData.Emit(new() { Status = PlayerStatusEnum.Loading });

        OpenMediaHandler? openHandler = _openHandler;
        if (openHandler is null)
        {
            return;
        }

        // The file itself is opened by whoever owns the engine; what it is, where in it to start and what the
        // status says around it is decided here.
        ErrorResult err = await openHandler(media, startPositionMs);
        if (!err.IsSuccessful)
        {
            PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerStatusEnum.Error,
                Description = err.DetailedErrorMessage,
            });
        }
    }

    /// <summary>
    /// The media the page is opening: what the panel and the title show, the history write and the favorite
    /// status. Opening the file itself is the page's, since it is the one that owns the engine.
    /// </summary>
    private async Task SetMedia(MediaModel? media)
    {
        _media = null;
        BackgroundImageUri = null;
        DurationMs = 0;

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
                    if (!AppSettingsModel.RestoreLastPlaybackPosition)
                    {
                        return 0;
                    }

                    long positionMs = media.LastPositionMs;
                    long remainingMs = media.DurationMs - positionMs;
                    return remainingMs >= 0 && remainingMs < ALMOST_ENDED_THRESHOLD_MS ? 0 : positionMs;
                }
        }
    }

    private void UpdateFavoriteStatusInternal(MediaModel media)
    {
        bool isFavorite = !media.IsExternal && FavoriteModel.Instance.FromId(media.Id) != null;
        SetFavorite(isFavorite, false);
    }

    #endregion

    #region Media state

    private void SaveVolumeAndMute()
    {
        if (_volumeSaveScheduled)
        {
            return;
        }

        _volumeSaveScheduled = true;
        CoroutineUtils.Run(async () =>
        {
            await Task.Delay(VOLUME_SAVE_DELAY_MS);
            _volumeSaveScheduled = false;

            AppSettingsModel.PlayerVolume = VolumeLiveData.Value / 100.0;
            AppSettingsModel.PlayerMuted = MuteLiveData.Value;
        });
    }

    /// <summary>The item just ended, so the media keeps the mark that it was played to the end.</summary>
    public void CompleteMedia()
    {
        MediaModel? media = _media;
        if (media is not null && !media.IsExternal)
        {
            CoroutineUtils.Run(async () => await media.SetProgress(100, 0));
        }
    }

    #endregion

    #region Types

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

    #endregion
}
