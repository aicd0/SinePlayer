// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.HotKey;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Data.Tables;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.SDK.Models;
using SinePlayer.UserControls.PlayerSettings;
using SinePlayer.Views.Pages.Main.Sidebar;
using SinePlayer.Views.Pages.Sidebar.MediaInfo;

namespace SinePlayer.Views.Pages.Player;

internal sealed partial class PlayerPage : BasePage
{
    #region Constructor

    private const long SEEK_STEP_MS = 5000;
    private const int VOLUME_STEP = 5;
    private const int SINGLE_TAP_DELAY_MS = 100;
    private const int HIDE_OVERLAY_DELAY_MS = 0;
    private const long ALMOST_ENDED_THRESHOLD_MS = 5000;

    // Can only be accessed on UI thread
    public static IReadOnlyList<Tuple<int, string>> ActiveTabs { get; private set; } = [];

    private readonly PlayerPageViewModel ViewModel = new();

    private PlayerNavigationBar? _navigationBar = null;

    public PlayerPage()
    {
        InitializeComponent();
        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
    }

    #endregion

    #region Lifecycle

    private bool _restoredFromLastSession;

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);
        AddToActiveTabs();

        InitializeNavigationBar();
        InitializeTransportBar();
        InitializePinState();

        ViewModel.Initialize(PageActionHandler, OpenMedia);
        ObserveData();
        UpdateKeyboardAccelerators();

        bool restored = bundle.GetString(RouterConstants.ARG_RESTORED) == "1";
        string playQueueId = bundle.GetString(RouterConstants.ARG_PLAY_QUEUE_ID) ?? string.Empty;
        string serializedPlayback = bundle.GetString(RouterConstants.ARG_PLAYBACK) ?? string.Empty;
        long initialPositionMs = -1;
        string? positionString = bundle.GetString(RouterConstants.ARG_POSITION);
        if (!string.IsNullOrEmpty(positionString))
        {
            long.TryParse(positionString, CultureInfo.InvariantCulture, out initialPositionMs);
        }

        _restoredFromLastSession = restored;

        VideoHost.Children.Clear();
        VideoHost.Children.Add(EnsureEngine().CreateVideoSurface());

        CoroutineUtils.Run(async () =>
        {
            PlayQueueModel playQueue = await GetPlayQueue(playQueueId);
            ViewModel.LoadPlayQueue(playQueue, serializedPlayback, initialPositionMs);
        });
    }

    protected override void OnResume()
    {
        base.OnResume();
        AddToActiveTabs();
        UpdateOverlayState();
        UpdatePageState();
        SyncCurrentMedia();
        FocusPlayer();
    }

    protected override void OnPause()
    {
        base.OnPause();
        UpdatePageState();
    }

    protected override void OnStop()
    {
        base.OnStop();

        ViewModel.SavePosition();
        DisposeEngine();
        ViewModel.Destory();

        _navigationBar = null;
        GetMainPageAbility().SetCustomCenteredNavigationBar(null);
        RemoveFromActiveTabs();
    }

    private void InitializeNavigationBar()
    {
        var bar = new PlayerNavigationBar();
        bar.Initialize(GetMainWindowAbility());
        bar.FavoriteChanged += isFavorite => ViewModel.SetFavorite(isFavorite, true);
        bar.MediaInfoRequested += OpenMediaInfoPane;
        bar.PlaybackInfoRequested += ShowPlaybackInfo;
        bar.PlayerSettingsChanged += ApplyPlayerSettings;
        GetMainPageAbility().SetCustomCenteredNavigationBar(bar);
        _navigationBar = bar;
    }

    private void InitializeTransportBar()
    {
        TransportBar.PlayPauseRequested += TogglePlayPause;
        TransportBar.PreviousRequested += ViewModel.Previous;
        TransportBar.NextRequested += ViewModel.Next;
        TransportBar.PlayQueueRequested += OpenPlayQueuePane;
        TransportBar.RepeatRequested += ViewModel.CycleRepeatMode;
        TransportBar.ShuffleRequested += ViewModel.ToggleShuffle;
        TransportBar.SeekRequested += SeekTo;
        TransportBar.VolumeChanged += SetVolume;
        TransportBar.MuteChanged += SetMuted;
        TransportBar.PinRequested += TogglePin;
        TransportBar.FullscreenRequested += ToggleFullscreen;
        TransportBar.SizeChanged += TransportBar_SizeChanged;
    }

    private void InitializePinState()
    {
        bool pinned = AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValueOrDefault(RegistryNames.StateKey.PLAYER_OVERLAY_PINNED, false);
        ViewModel.SetPinned(pinned);
        TransportBar.SetPinned(pinned);
        UpdatePinRelatedUI();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.FavoriteUpdated.Observe(this, _ => ViewModel.UpdateFavoriteStatus());

        GlobalEvent.Instance.MediaUpdated.Observe(this, ids =>
        {
            MediaModel? media = ViewModel.Media;
            if (media is null || !ids.Contains(media.Id))
            {
                return;
            }

            ViewModel.UpdateMediaMetadata();
        });

        KeyboardShortcutManager.ShortcutsChangedLiveData.Observe(this, _ => UpdateKeyboardAccelerators());

        AppSettingsModel.KeepScreenOnBehaviorChangedLiveData.Observe(this, _ => UpdateKeepScreenOn());

        GetWindowEventBus().With<double>(EventId.TopOverlayHeight).ObserveSticky(this, h =>
        {
            if (_topOverlayHeight == h)
            {
                return;
            }

            _topOverlayHeight = h;

            if (ViewModel.IsPinned)
            {
                UpdatePinRelatedUI();
            }
        });

        GetWindowEventBus().With<double>(EventId.RightOverlayWidth).ObserveSticky(this, w =>
        {
            if (_rightOverlayWidth == w)
            {
                return;
            }

            _rightOverlayWidth = w;

            TransportBar.Margin = new Thickness(0, 0, w, 0);

            if (ViewModel.IsPinned)
            {
                UpdatePinRelatedUI();
            }
        });

        GetWindowEventBus().With<double>(EventId.TitleBarOpacity).ObserveSticky(this, opacity =>
        {
            TransportBar.Opacity = opacity;

            bool autoToggleOverlaysOnCursor = AppSettingsModel.AutoToggleOverlaysOnCursor;
            TransportBar.IsHitTestVisible = autoToggleOverlaysOnCursor || opacity >= 0.5;
            GetMainPageAbility().SetHiddenOverlayHitTestVisibility(autoToggleOverlaysOnCursor);
        });

        GetMainWindowAbility().RegisterFullscreenChangedHandler(this, isFullscreen =>
        {
            ViewModel.SetFullscreen(isFullscreen);
            TransportBar.SetFullscreen(isFullscreen);
        });

        GetMainWindowAbility().RegisterPointerInsideRootElementChangedHandler(this, isInside =>
        {
            _isPointerInsideRootElement = isInside;
            UpdateOverlayState();
        });

        GetMainPageAbility().RegisterOverlayVisibilityChangedHandler(this, visible =>
        {
            if (visible)
            {
                ShowOverlay();
            }
            else
            {
                HideOverlay();
            }
        });

        ViewModel.PlaybackChangeLiveData.Observe(this, _ =>
        {
            TransportBar.SetNavigation(ViewModel.IsPlaybackPreviousEnabled, ViewModel.IsPlaybackNextEnabled);
            TransportBar.SetRepeatMode(ViewModel.Playback.RepeatMode);
            TransportBar.SetShuffle(ViewModel.Playback.IsShuffle);
            GetWindowEventBus().With<PlaybackModel>(EventId.PlaybackChanged).Emit(ViewModel.Playback);

            MediaModel? media = ViewModel.Media;
            if (media is null)
            {
                GetMainPageAbility().SetTitle(string.Empty);
                GetMainPageAbility().SetIcon(new SymbolIconSource { Symbol = Symbol.Play });
            }
            else
            {
                GetMainPageAbility().SetTitle(media.Title);
                GetMainPageAbility().SetIcon(new FontIconSource { Glyph = MediaGlyphHelper.GetMediaGlyph(media) });
                _navigationBar?.SetPlayerSettings(media);
            }

            Route route = Route.Create(GetMainPageAbility().Url)
                .WithParam(RouterConstants.ARG_PLAYBACK, ViewModel.Playback.ToSerializedString());
            GetMainPageAbility().SetUrl(route.Url);

            SyncCurrentMedia();
        }, new ObserveOptions()
        {
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.PlayerStatusLiveData.Observe(this, status =>
        {
            switch (status.Status)
            {
                case PlayerPageViewModel.PlayerStatusEnum.Working:
                    StatusPanel.Visibility = Visibility.Collapsed;
                    StatusText.Text = string.Empty;
                    break;
                case PlayerPageViewModel.PlayerStatusEnum.Loading:
                    StatusPanel.Visibility = Visibility.Visible;
                    StatusText.Text = StringResource.PlayerStatusLoading;
                    break;
                case PlayerPageViewModel.PlayerStatusEnum.Error:
                    StatusPanel.Visibility = Visibility.Visible;
                    StatusText.Text = status.Description;
                    break;
                default:
                    break;
            }

            if (status.Status == PlayerPageViewModel.PlayerStatusEnum.Error)
            {
                ShowOverlay();
            }
        });

        ViewModel.IsPlayingLiveData.Observe(this, TransportBar.SetPlaying);

        ViewModel.PositionLiveData.Observe(this, positionMs =>
        {
            TransportBar.SetPosition(positionMs, ViewModel.DurationMs);
        });

        ViewModel.IsFavoriteLiveData.Observe(this, isFavorite =>
        {
            _navigationBar?.SetFavorite(isFavorite);
        });

        ViewModel.IsExternalMediaLiveData.Observe(this, isExternal =>
        {
            _navigationBar?.SetExternalMedia(isExternal);
        });

        ViewModel.VolumeLiveData.Observe(this, TransportBar.SetVolume);

        ViewModel.MuteLiveData.Observe(this, TransportBar.SetMuted);
    }

    private async Task<PlayQueueModel> GetPlayQueue(string playQueueId)
    {
        string playQueuesRegistry = $"{RegistryNames.TAB_RESOURCES}/{GetMainPageAbility().TabId}/PlayQueues";

        PlayQueueModel? playQueue = null;
        if (!string.IsNullOrEmpty(playQueueId))
        {
            if (AppDB.MainRegistry.TryGetKey(RegistryNames.PLAY_QUEUES, out IRegistryKey? key))
            {
                if (key.TryGet(playQueueId, out string? serializedPlayQueue))
                {
                    playQueue = await PlayQueueModel.CreateFromSerializedString(serializedPlayQueue);
                }
            }

            if (playQueue is null && AppDB.MainRegistry.TryGetKey(playQueuesRegistry, out key))
            {
                if (key.TryGet(playQueueId, out string? serializedPlayQueue))
                {
                    playQueue = await PlayQueueModel.CreateFromSerializedString(serializedPlayQueue);
                    if (playQueue is not null)
                    {
                        return playQueue;
                    }
                }
            }
        }
        else
        {
            playQueueId = Guid.NewGuid().ToString();
            Route route = Route.Create(GetMainPageAbility().Url)
                .WithParam(RouterConstants.ARG_PLAY_QUEUE_ID, playQueueId);
            GetMainPageAbility().SetUrl(route.Url);
        }

        playQueue ??= PlayQueueModel.CreateEmpty();
        AppDB.MainRegistry.CreateKey(playQueuesRegistry).Set(playQueueId, playQueue.ToSerializedString());
        return playQueue;
    }

    #endregion

    #region Common events

    private void VideoHost_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        args.Handled = true;

        CoroutineUtils.Run(async () =>
        {
            PlayerSettingsPanel? settingsPanel = _navigationBar?.PlayerSettingsPanel;
            MediaModel? media = ViewModel.Media;
            if (settingsPanel is null || media is null)
            {
                return;
            }

            List<BaseMenuFlyoutItemModel> items = await PlayerMenuCreator.CreateVideoHostMenuItems(
                PageActionHandler,
                media,
                settingsPanel.Settings,
                settingsPanel.Apply,
                ViewModel.PlayQueue.ToBuilder(),
                ViewModel.Playback.ToBuilder());

            MenuFlyout flyout = new();
            foreach (BaseMenuFlyoutItemModel item in items)
            {
                flyout.Items.Add(item.CreateMenuFlyoutItem());
            }

            if (args.TryGetPosition(fe, out Windows.Foundation.Point point))
            {
                flyout.ShowAt(fe, new FlyoutShowOptions { Position = point });
            }
            else
            {
                flyout.ShowAt(fe);
            }
        });
    }

    private void TransportBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_bottomGridHeight == e.NewSize.Height)
        {
            return;
        }

        _bottomGridHeight = e.NewSize.Height;

        if (ViewModel.IsPinned)
        {
            UpdatePinRelatedUI();
        }
    }

    #endregion

    #region Engine

    private CoreEngine? _engine;
    private PlaybackComponents _components = PlaybackComponents.Default;
    private VideoTransform _videoTransform = VideoTransform.Identity;
    private long _pendingStartPositionMs = -1;

    private CoreEngine EnsureEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        CoreEngine engine = new();
        engine.SetVolume(Math.Clamp(AppSettingsModel.PlayerVolume, 0.0, 1.0));
        engine.SetMuted(AppSettingsModel.PlayerMuted);
        engine.StateChanged += Engine_StateChanged;
        engine.PositionChanged += Engine_PositionChanged;
        engine.MediaOpened += Engine_MediaOpened;
        engine.MediaEnded += Engine_MediaEnded;
        engine.ErrorOccurred += Engine_ErrorOccurred;
        _engine = engine;
        ViewModel.VolumeLiveData.Emit((int)Math.Round(engine.Volume * 100.0));
        ViewModel.MuteLiveData.Emit(engine.IsMuted);
        return engine;
    }

    private void DisposeEngine()
    {
        CoreEngine? engine = _engine;
        _engine = null;
        if (engine is null)
        {
            return;
        }

        engine.StateChanged -= Engine_StateChanged;
        engine.PositionChanged -= Engine_PositionChanged;
        engine.MediaOpened -= Engine_MediaOpened;
        engine.MediaEnded -= Engine_MediaEnded;
        engine.ErrorOccurred -= Engine_ErrorOccurred;
        engine.Dispose();
    }

    private void Engine_StateChanged(MediaEngineState state)
    {
        ViewModel.IsPlayingLiveData.Emit(state == MediaEngineState.Playing);

        switch (state)
        {
            case MediaEngineState.Playing:
                ViewModel.IsPlaying = true;
                break;
            case MediaEngineState.None:
            case MediaEngineState.Paused:
            case MediaEngineState.Ended:
            case MediaEngineState.Error:
                ViewModel.IsPlaying = false;
                break;
            default:
                break;
        }

        UpdateKeepScreenOn();
    }

    private void Engine_PositionChanged()
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        ViewModel.SetPosition(
            engine.Position.Ticks / TimeSpan.TicksPerMillisecond,
            engine.Duration.Ticks / TimeSpan.TicksPerMillisecond);
    }

    private void Engine_MediaOpened()
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        ViewModel.SetDuration(engine.Duration.Ticks / TimeSpan.TicksPerMillisecond);

        long startPositionMs = _pendingStartPositionMs;
        _pendingStartPositionMs = -1;
        if (startPositionMs > 0)
        {
            engine.Seek(TimeSpan.FromMilliseconds(startPositionMs));
        }

        ViewModel.PositionLiveData.Emit(startPositionMs > 0 ? startPositionMs : 0);
        ViewModel.PlayerStatusLiveData.Emit(new() { Status = PlayerPageViewModel.PlayerStatusEnum.Working });

        bool restoredFromLastSession = _restoredFromLastSession;
        _restoredFromLastSession = false;

        if (AppSettingsModel.AutoPlayOnOpen && !(restoredFromLastSession && AppSettingsModel.AutoPlayOnOpenExceptAppLaunch))
        {
            if (IsResumed)
            {
                engine.Play();
            }
            else
            {
                _playbackPendingOnActivate = true;
            }
        }
    }

    private void Engine_MediaEnded()
    {
        ViewModel.CompleteMedia();

        if (ViewModel.Playback.RepeatMode == RepeatMode.One)
        {
            _engine?.Play();
            return;
        }

        if (AppSettingsModel.AutoPlayNext)
        {
            ViewModel.Next();
        }
    }

    private void Engine_ErrorOccurred(string message)
    {
        ViewModel.PlayerStatusLiveData.Emit(new()
        {
            Status = PlayerPageViewModel.PlayerStatusEnum.Error,
            Description = message,
        });
    }

    /// <summary>
    /// Opens a file through the engine the page owns. Which file, and where in it to start, are the view model's
    /// decision; this is the engine half: let go of what is open, be told what to play through, open the file.
    /// </summary>
    private async Task<ErrorResult> OpenMedia(MediaModel media, long startPositionMs)
    {
        CoreEngine engine = EnsureEngine();

        // Where the file that is about to be replaced was left: the view model keeps that mark, and it still knows
        // the previous media is the one playing.
        ViewModel.SavePosition();

        _pendingStartPositionMs = startPositionMs;

        // What the media was last played through, which is what its settings were saved with.
        var mediaSettings = PlayerSettingsModel.LoadFromMedia(media);
        _components = mediaSettings.ToComponents();
        _videoTransform = mediaSettings.ToVideoTransform();

        // Components are only read when the file is opened, so the engine lets go of what it has before it is
        // told what to use, and then opens the new file with it.
        await engine.Stop();
        engine.SetComponents(_components);
        engine.SetVideoTransform(_videoTransform);
        return await engine.Load(media.Location);
    }

    #endregion

    #region Gesture

    private readonly PlayerGestureRecognizer _gestureRecognizer = new();
    private bool _tapPending = false;
    private bool _tapCancelled = false;

    private void VideoHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        FocusPlayer();
        ((UIElement)sender).CapturePointer(e.Pointer);
        _gestureRecognizer.ProcessDownEvent(e.GetCurrentPoint(VideoHost));
    }

    private void VideoHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _gestureRecognizer.ProcessMoveEvents(e.GetIntermediatePoints(VideoHost));
    }

    private void VideoHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _gestureRecognizer.ProcessUpEvent(e.GetCurrentPoint(VideoHost));
    }

    private void VideoHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _gestureRecognizer.ProcessUpEvent(e.GetCurrentPoint(VideoHost));
    }

    private void GestureRecognizer_Tapped(GestureRecognizer sender, TappedEventArgs e)
    {
        if (e.TapCount == 1)
        {
            if (_tapPending)
            {
                return;
            }

            _tapPending = true;
            _tapCancelled = false;
            CoroutineUtils.Run(async () =>
            {
                await Task.Delay(SINGLE_TAP_DELAY_MS);

                _tapPending = false;
                if (_tapCancelled || !IsStarted)
                {
                    return;
                }

                SetOverlayHold(!_isOverlayVisible);
            });
        }
        else if (e.TapCount == 2)
        {
            _tapCancelled = true;
            TogglePlayPause();
        }
    }

    private void VideoHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInsideVideo = true;
        UpdateOverlayState();
    }

    private void VideoHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInsideVideo = false;
        UpdateOverlayState();
    }

    #endregion

    #region Overlay

    private bool _shouldOverlayVisible = false;
    private bool _isPointerInsideRootElement = true;
    private bool _isPointerInsideVideo = false;
    private double _topOverlayHeight = 0.0;
    private double _rightOverlayWidth = 0.0;
    private double _bottomGridHeight = 0.0;
    private bool _isOverlayVisible = false;
    private bool _isOverlayHold = false;
    private long _hideOverlayDeadline = -1;

    private void UpdateOverlayState()
    {
        bool GetPointerInsideWindowState()
        {
            SizeF windowSize = GetMainWindowAbility().WindowSize;
            PointF pointerPos = GetMainWindowAbility().GetPointerPosition();
            const float padding = 10.0F;
            return
                pointerPos.X >= padding &&
                pointerPos.X <= windowSize.Width - padding &&
                pointerPos.Y >= padding &&
                pointerPos.Y <= windowSize.Height - padding;
        }

        bool isPointerInsideWindow = GetPointerInsideWindowState();
        bool shouldOverlayVisible = (isPointerInsideWindow || _isPointerInsideRootElement) && !_isPointerInsideVideo;
        if (shouldOverlayVisible == _shouldOverlayVisible)
        {
            return;
        }

        _shouldOverlayVisible = shouldOverlayVisible;

        if (AppSettingsModel.AutoToggleOverlaysOnCursor)
        {
            if (shouldOverlayVisible)
            {
                ShowOverlay();
            }
            else
            {
                TryHideOverlay(HIDE_OVERLAY_DELAY_MS);
            }
        }
    }

    private void TryHideOverlay(int delayMilliseconds = 0)
    {
        if (!_isOverlayVisible ||
            ViewModel.IsPinned ||
            _isOverlayHold ||
            _shouldOverlayVisible)
        {
            return;
        }

        if (delayMilliseconds > 0)
        {
            void PostHideTask(int delay)
            {
                CoroutineUtils.Run(async () =>
                {
                    await Task.Delay(delay);

                    if (_hideOverlayDeadline == -1)
                    {
                        return;
                    }

                    long currentTick = GetTick();
                    if (currentTick < _hideOverlayDeadline)
                    {
                        PostHideTask((int)(_hideOverlayDeadline - currentTick));
                        return;
                    }

                    TryHideOverlay();
                });
            }

            _hideOverlayDeadline = GetTick() + delayMilliseconds;
            PostHideTask(delayMilliseconds);
            return;
        }

        HideOverlay();
    }

    private void ShowOverlay()
    {
        _hideOverlayDeadline = -1;

        if (_isOverlayVisible)
        {
            return;
        }

        _isOverlayVisible = true;
        GetMainPageAbility().SetOverlayVisibility(true);
    }

    private void HideOverlay()
    {
        _hideOverlayDeadline = -1;

        if (!_isOverlayVisible)
        {
            return;
        }

        _isOverlayVisible = false;
        _isOverlayHold = false;
        GetMainPageAbility().SetOverlayVisibility(false);
    }

    private void SetOverlayHold(bool hold)
    {
        _isOverlayHold = hold;

        if (hold)
        {
            ShowOverlay();
        }
        else
        {
            TryHideOverlay();
        }
    }

    private void UpdatePinRelatedUI()
    {
        if (ViewModel.IsPinned)
        {
            VideoHost.Margin = new Thickness(0, _topOverlayHeight, _rightOverlayWidth, _bottomGridHeight);
            ShowOverlay();
        }
        else
        {
            VideoHost.Margin = new Thickness(0);
        }
    }

    #endregion

    #region Playback control

    private void TogglePlayPause()
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        if (engine.State == MediaEngineState.Playing)
        {
            engine.Pause();
        }
        else
        {
            engine.Play();
        }
    }

    private void SeekBy(long deltaMs)
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        long positionMs = engine.Position.Ticks / TimeSpan.TicksPerMillisecond;
        SeekTo(Math.Max(positionMs + deltaMs, 0));
    }

    private void SeekTo(long positionMs)
    {
        _engine?.Seek(TimeSpan.FromMilliseconds(positionMs));
    }

    private void SetVolume(int volume)
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        int clampedVolume = Math.Clamp(volume, 0, 100);
        engine.SetVolume(clampedVolume / 100.0);
        ViewModel.SetVolume(clampedVolume);
    }

    private void AdjustVolume(int delta)
    {
        SetVolume(ViewModel.VolumeLiveData.Value + delta);
    }

    private void SetMuted(bool isMuted)
    {
        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        engine.SetMuted(isMuted);
        ViewModel.SetMute(isMuted);
    }

    private void ApplyPlayerSettings(PlayerSettingsModel settings)
    {
        ApplyVideoTransform(settings);

        PlaybackComponents components = settings.ToComponents();
        if (components == _components)
        {
            return;
        }

        _components = components;

        // A file that is being opened reads the choice as it opens, so the engine is told only once it is not
        // mid-open.
        if (ViewModel.IsLoading)
        {
            return;
        }

        // A component change is not a reason to open the file again: the pipeline swaps the chains the choice
        // affects and keeps playing where it was, as the playback lab does.
        _engine?.SetComponents(components);
    }

    /// <summary>
    /// Where the picture goes is not a component: nothing is rebuilt and no frame is waited for, the picture that
    /// is already there is drawn again — so this is applied whatever the engine is doing, including mid-open.
    /// </summary>
    private void ApplyVideoTransform(PlayerSettingsModel settings)
    {
        var transform = settings.ToVideoTransform();
        if (transform == _videoTransform)
        {
            return;
        }

        _videoTransform = transform;
        _engine?.SetVideoTransform(transform);
    }

    #endregion

    #region Page state

    private bool _isKeepScreenOnActive;
    private bool _playbackPendingOnActivate;

    private void UpdatePageState()
    {
        UpdateKeepScreenOn();

        if (!IsResumed)
        {
            if (AppSettingsModel.PauseOnLeaveTab)
            {
                _engine?.Pause();
            }

            ViewModel.SavePosition();
        }

        if (IsResumed && _playbackPendingOnActivate)
        {
            _playbackPendingOnActivate = false;
            _engine?.Play();
        }
    }

    private void UpdateKeepScreenOn()
    {
        bool shouldKeepScreenOn = IsResumed && ShouldKeepScreenOn();
        if (_isKeepScreenOnActive == shouldKeepScreenOn)
        {
            return;
        }

        _isKeepScreenOnActive = shouldKeepScreenOn;
        if (shouldKeepScreenOn)
        {
            DisplayRequestManager.IncrememtKeepScreenOn();
        }
        else
        {
            DisplayRequestManager.DecrememtKeepScreenOn();
        }
    }

    private bool ShouldKeepScreenOn()
    {
        MediaModel? media = ViewModel.Media;
        return AppSettingsModel.KeepScreenOnBehavior switch
        {
            AppSettingsModel.KeepScreenOnBehaviorEnum.Never => false,
            AppSettingsModel.KeepScreenOnBehaviorEnum.Always => true,
            AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlaying => _engine?.State == MediaEngineState.Playing,
            AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlayingVideos => _engine?.State == MediaEngineState.Playing && media is not null && media.Type == MediaType.Video,
            _ => false,
        };
    }

    #endregion

    #region Misc

    private void FocusPlayer()
    {
        Focus(FocusState.Programmatic);
    }

    private void TogglePin()
    {
        ViewModel.SetPinned(!ViewModel.IsPinned);
        TransportBar.SetPinned(ViewModel.IsPinned);
        UpdatePinRelatedUI();
        AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.PLAYER_OVERLAY_PINNED, ViewModel.IsPinned);
    }

    private void OpenPlayQueuePane()
    {
        GetMainPageAbility().SetSidePanePage(SidebarView.ITEM_PLAY_QUEUE);
        GetMainPageAbility().SetSidePaneOpenState(true, force: true);
    }

    private void OpenMediaInfoPane()
    {
        GetMainPageAbility().SetSidePanePage(SidebarView.ITEM_MEDIA_INFO);
        GetMainPageAbility().SetSidePaneOpenState(true, force: true);
    }

    private void ShowPlaybackInfo()
    {
        GetMainPageAbility().SetSidePanePage(SidebarView.ITEM_PLAYBACK_INFO);
        GetMainPageAbility().SetSidePaneOpenState(true, force: true);
    }

    private void SyncCurrentMedia()
    {
        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaChanged).Emit(new()
        {
            Media = ViewModel.Media,
            PlayQueue = ViewModel.PlayQueue,
            ImageDescriptions = [],
            PlaybackInfo = _engine?.PlaybackInfo,
        });
    }

    private void ToggleFullscreen()
    {
        IMainWindowAbility ability = GetMainWindowAbility();
        if (ability.IsFullscreen)
        {
            ability.ExitFullscreen();
        }
        else
        {
            ability.EnterFullscreen();
        }
    }

    private void UpdateKeyboardAccelerators()
    {
        void AddKeyboardAccelerators(string action, Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
        {
            foreach (KeyboardAccelerator accelerator in KeyboardShortcutManager.CreateAccelerators(action, handler))
            {
                KeyboardAccelerators.Add(accelerator);
            }
        }

        KeyboardAccelerators.Clear();

        AddKeyboardAccelerators(KeyboardShortcutActions.PlayPause, (_, _) => TogglePlayPause());
        AddKeyboardAccelerators(KeyboardShortcutActions.SeekBackward, (_, _) => SeekBy(-SEEK_STEP_MS));
        AddKeyboardAccelerators(KeyboardShortcutActions.SeekForward, (_, _) => SeekBy(SEEK_STEP_MS));
        AddKeyboardAccelerators(KeyboardShortcutActions.VolumeDown, (_, _) => AdjustVolume(-VOLUME_STEP));
        AddKeyboardAccelerators(KeyboardShortcutActions.VolumeUp, (_, _) => AdjustVolume(VOLUME_STEP));
        AddKeyboardAccelerators(KeyboardShortcutActions.OpenNextItem, (_, _) => ViewModel.Next());
        AddKeyboardAccelerators(KeyboardShortcutActions.OpenPreviousItem, (_, _) => ViewModel.Previous());
    }

    #endregion

    #region Utilities

    private IMainPageAbilityForTab GetMainPageAbility()
    {
        return GetAbility<IMainPageAbilityForTab>()!;
    }

    private IMainWindowAbility GetMainWindowAbility()
    {
        return GetAbility<IMainWindowAbility>()!;
    }

    private static long GetTick()
    {
        return Environment.TickCount;
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

    #endregion
}
