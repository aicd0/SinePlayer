// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Data.Models.Playback;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;

namespace SinePlayerUWP.Views.Pages.Reader;

internal partial class ReaderPageViewModel : INotifyPropertyChanged
{
    private ActionHandler _actionHandler = ActionHandler.Dummy;

    // Media Status
    private MediaModel? _media;
    private LoadMediaArgs? _pendingMedia;
    private bool _isLoading = false;
    private bool? _isFavorite = null;

    public ReaderPageViewModel() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public readonly MutableLiveData<string> TitleLiveData = new();
    public readonly MutableLiveData<bool> PlaybackChangeLiveData = new();
    public readonly MutableLiveData<ReaderStatusInfo> ReaderStatusLiveData = new(new() { Status = ReaderStatusEnum.Loading });
    public readonly MutableLiveData<bool> MediaChangedLiveData = new();
    public readonly MutableLiveData<bool> IsExternalMediaLiveData = new(true);
    public readonly MutableLiveData<bool> IsFavoriteLiveData = new();

    private FlowDirection _preferredFlowDirection = FlowDirection.LeftToRight;
    public FlowDirection PreferredFlowDirection
    {
        get => _preferredFlowDirection;
        set
        {
            _preferredFlowDirection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreferredFlowDirection)));
        }
    }

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

    private string _zooming = string.Empty;
    public string Zooming
    {
        get => _zooming;
        set
        {
            _zooming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Zooming)));
        }
    }

    private string _primaryPageIndicatorText = string.Empty;
    public string PrimaryPageIndicatorText
    {
        get => _primaryPageIndicatorText;
        set
        {
            _primaryPageIndicatorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryPageIndicatorText)));
        }
    }

    private string _secondaryPageIndicatorText = string.Empty;
    public string SecondaryPageIndicatorText
    {
        get => _secondaryPageIndicatorText;
        set
        {
            _secondaryPageIndicatorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryPageIndicatorText)));
        }
    }

    private bool _isAutoPlayEnabled = false;
    public bool IsAutoPlayEnabled
    {
        get => _isAutoPlayEnabled;
        set
        {
            _isAutoPlayEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAutoPlayEnabled)));
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
    public ReaderStatusEnum ReaderStatus => ReaderStatusLiveData.Value.Status;
    public MediaModel? Media => _media;

    public void Initialize(ActionHandler actionHandler)
    {
        _actionHandler = actionHandler;
        Playback.PlaybackStateChanged += Playback_PlaybackStateChanged;
    }

    public void Destory()
    {
        Playback.PlaybackStateChanged -= Playback_PlaybackStateChanged;
    }

    public void LoadPlaylist(PlaylistModel playlist, string? serializedPlayback, double initialPage)
    {
        Playlist = playlist;
        Playback.LoadState(playlist, serializedPlayback, initialPage);
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

    public void SetZooming(int zooming)
    {
        Zooming = $"{zooming}%";
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
            ReaderStatusLiveData.Emit(new()
            {
                Status = ReaderStatusEnum.Error,
                Description = "The reading list is empty.",
            });
        }
        else if (playlistItem.Media != _media || args.Reason == PlaybackStateChangeReason.Refresh)
        {
            TitleLiveData.Emit(playlistItem.Media.Title);
            ReaderStatusLiveData.Emit(new() { Status = ReaderStatusEnum.Loading });
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
        MediaModel? media = args.Media;

        // Clear previous media
        _media = null;
        BackgroundImageUri = null;

        // Load new media
        if (media is null)
        {
            ReaderStatusLiveData.Emit(new()
            {
                Status = ReaderStatusEnum.Error,
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

        // Media playback will be provided by the media engine in a later migration phase;
        // it will load the media file from media.Location and resume from the stored position.
        ReaderStatusLiveData.Emit(new()
        {
            Status = ReaderStatusEnum.Error,
            Description = "Media playback is not implemented yet.",
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

    public enum ReaderStatusEnum
    {
        Loading,
        Error,
        Working,
    }

    public class ReaderStatusInfo
    {
        public required ReaderStatusEnum Status { get; init; }
        public string Description { get; init; } = string.Empty;
        public int Progress { get; init; } = -1;
    }

    private class LoadMediaArgs
    {
        public required MediaModel? Media { get; init; }
        public required PlaybackStateChangedEventArgs PlaybackArgs { get; init; }
    }
}
