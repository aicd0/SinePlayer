// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.HotKey;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Playback.Core;
using SinePlayer.Common.Plugins;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
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
        SubtitleHost.Children.Clear();
        SubtitleHost.Children.Add(EnsureEngine().CreateSubtitleSurface());

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
        ViewModel.Destory();
        DisposeEngine();
        StopPromptTimer();
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
            UpdatePromptMargin();

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
            if (status.Status == PlayerPageViewModel.PlayerStatusEnum.Error)
            {
                PromptError(status.Description);
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

            PlayerMenuContext context = new()
            {
                ActionHandler = PageActionHandler,
                Media = media,
                Settings = settingsPanel.Settings,
                UpdateSettings = settingsPanel.Apply,
                PlayQueue = ViewModel.PlayQueue.ToBuilder(),
                Playback = ViewModel.Playback.ToBuilder(),
                Engine = _engine,
                PromptInfo = PromptInfo,
            };

            List<BaseMenuFlyoutItemModel> items = await PlayerMenuCreator.CreateVideoHostMenuItems(context);

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

    private const string TAG = nameof(PlayerPage);

    private CoreEngine? _engine;
    private PlaybackComponents _components = PlaybackComponents.Default;
    private VideoTransform _videoTransform = VideoTransform.Identity;

    /// <summary>
    /// Opens a file through the engine the page owns, and starts it. Which file, and where in it to start, are the
    /// view model's decision; what the file is played through — which tracks, and whether it plays at all — is the
    /// page's, because the engine opens a file and no more.
    /// </summary>
    private async Task<OperationResult> OpenMedia(MediaModel media, long startPositionMs)
    {
        CoreEngine engine = EnsureEngine();

        // Where the file that is about to be replaced was left: the view model keeps that mark, and it still knows
        // the previous media is the one playing.
        ViewModel.SavePosition();

        // What the media was last played through, which is what its settings were saved with.
        var mediaSettings = PlayerSettingsModel.LoadFromMedia(media);
        _components = mediaSettings.ToComponents();
        _videoTransform = mediaSettings.ToVideoTransform();

        // Components are only read when the file is opened, so the engine lets go of what it has before it is
        // told what to use, and then opens the new file with it. A component that cannot be applied is reported by
        // the open itself, which has nothing to build its chains from.
        await engine.Stop();
        await engine.SetComponents(_components);
        engine.SetVideoTransform(_videoTransform);

        OperationResult loaded = await engine.Load(media.Location);
        if (!loaded.IsSuccessful)
        {
            return loaded;
        }

        // The order of the three steps below is the order they need: a track can only be chosen once there is a file,
        // the seek asks for a position on the tracks that will play it, and playing before either is a Play the
        // pipeline refuses for having nothing to play.
        OperationResult selected = await SelectTracks(engine, media);
        if (!selected.IsSuccessful)
        {
            return selected;
        }

        if (startPositionMs > 0)
        {
            SeekMode mode = AppSettingsModel.PreciseSeeking ? SeekMode.Precise : SeekMode.Fast;
            engine.Seek(TimeSpan.FromMilliseconds(startPositionMs), mode);
        }

        ViewModel.PositionLiveData.Emit(startPositionMs > 0 ? startPositionMs : 0);

        StartPlaybackOnOpen(engine);
        return selected;
    }

    private static async Task<OperationResult> SelectTracks(CoreEngine engine, MediaModel media)
    {
        static T? DefaultTrack<T>(IReadOnlyList<T>? streams) where T : MediaStreamInfo
        {
            if (streams is null || streams.Count == 0)
            {
                return null;
            }

            foreach (T stream in streams)
            {
                if (stream.IsDefault)
                {
                    return stream;
                }
            }

            return streams[0];
        }

        MediaInfo? info = engine.PlaybackInfo.Media;

        TrackSelection.Preference videoChoice = TrackSelection.Load(media, MediaStreamKind.Video, info);
        TrackSelection.Preference audioChoice = TrackSelection.Load(media, MediaStreamKind.Audio, info);
        TrackSelection.Preference subtitleChoice = TrackSelection.Load(media, MediaStreamKind.Subtitle, info);

        MediaStreamInfo? video = videoChoice.IsSet ? videoChoice.Stream : DefaultTrack(info?.VideoStreams);
        MediaStreamInfo? audio = audioChoice.IsSet ? audioChoice.Stream : DefaultTrack(info?.AudioStreams);

        if (video is null && audio is null && (videoChoice.IsSet || audioChoice.IsSet))
        {
            video = DefaultTrack(info?.VideoStreams);
            audio = DefaultTrack(info?.AudioStreams);
        }

        List<string> unplayable = [];
        bool playing = false;

        if (video is not null)
        {
            OperationResult chosen = await engine.SelectStream(MediaStreamKind.Video, video);
            playing |= chosen.IsSuccessful;
            if (!chosen.IsSuccessful)
            {
                unplayable.Add($"the picture could not be played ({chosen.Message})");
            }
        }

        if (audio is not null)
        {
            OperationResult chosen = await engine.SelectStream(MediaStreamKind.Audio, audio);
            playing |= chosen.IsSuccessful;
            if (!chosen.IsSuccessful)
            {
                unplayable.Add($"the sound could not be played ({chosen.Message})");
            }
        }

        // Subtitles are the one track that stays off until it is asked for: a file's own default subtitle is not a
        // reason to draw it over the picture, and only a choice made for this file is a reason to.
        if (subtitleChoice.Stream is not null)
        {
            OperationResult chosen = await engine.SelectStream(MediaStreamKind.Subtitle, subtitleChoice.Stream);
            if (!chosen.IsSuccessful)
            {
                unplayable.Add($"the subtitles could not be played ({chosen.Message})");
            }
        }

        if (!playing)
        {
            string why = unplayable.Count == 0 ? "it holds no track that could be played" : string.Join(", ", unplayable);
            return OperationResultExt.Failure(TAG, $"Unable to play media file '{media.Location}': {why}.");
        }

        if (unplayable.Count > 0)
        {
            Logger.W(TAG, $"Playing '{media.Location}' without everything it holds: {string.Join(", ", unplayable)}.");
        }

        return OperationResult.Success();
    }

    /// <summary>
    /// Whether an opened file starts playing on its own, which is a setting rather than a consequence of opening it.
    /// Asked after the tracks are chosen, so that "opens paused" means the user turned auto-play off rather than that
    /// nothing had been chosen yet.
    /// </summary>
    private void StartPlaybackOnOpen(CoreEngine engine)
    {
        bool restoredFromLastSession = _restoredFromLastSession;
        _restoredFromLastSession = false;

        if (!AppSettingsModel.AutoPlayOnOpen || (restoredFromLastSession && AppSettingsModel.AutoPlayOnOpenExceptAppLaunch))
        {
            return;
        }

        if (IsResumed)
        {
            engine.Play();
        }
        else
        {
            _playbackPendingOnActivate = true;
        }
    }

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
        engine.Dispose();
    }

    private void Engine_StateChanged(MediaEngineState state, string? message)
    {
        ViewModel.IsPlayingLiveData.Emit(state == MediaEngineState.Playing);

        switch (state)
        {
            case MediaEngineState.Playing:
                ViewModel.IsPlaying = true;
                break;
            case MediaEngineState.None:
            case MediaEngineState.Paused:
                ViewModel.IsPlaying = false;
                break;
            case MediaEngineState.Ended:
                ViewModel.IsPlaying = false;
                HandleMediaEnded();
                break;
            case MediaEngineState.Error:
                ViewModel.IsPlaying = false;
                ViewModel.PlayerStatusLiveData.Emit(new()
                {
                    Status = PlayerPageViewModel.PlayerStatusEnum.Error,
                    Description = message ?? "Unknown error",
                });
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
        ViewModel.PlayerStatusLiveData.Emit(new() { Status = PlayerPageViewModel.PlayerStatusEnum.Working });
    }

    private void HandleMediaEnded()
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
        UpdatePromptMargin();
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
        UpdatePromptMargin();
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
        SeekMode mode = AppSettingsModel.PreciseSeeking ? SeekMode.Precise : SeekMode.Fast;
        _engine?.Seek(TimeSpan.FromMilliseconds(positionMs), mode);
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

    private async void ApplyPlayerSettings(PlayerSettingsModel settings)
    {
        ApplyVideoTransform(settings);

        PlaybackComponents components = settings.ToComponents();
        if (components == _components)
        {
            return;
        }

        _components = components;

        if (ViewModel.IsLoading)
        {
            return;
        }

        CoreEngine? engine = _engine;
        if (engine is null)
        {
            return;
        }

        OperationResult err = await engine.SetComponents(components);
        if (!err.IsSuccessful)
        {
            PromptInfo(err.Message);
        }
    }

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

    #region Player prompt

    private const int PromptDurationMilliseconds = 3000;

    private DispatcherQueueTimer? _promptTimer;

    private void PromptInfo(string message)
    {
        ShowPrompt(message, autoHide: true);
    }

    private void PromptError(string message)
    {
        ShowPrompt(message, autoHide: false);
    }

    private void ShowPrompt(string message, bool autoHide)
    {
        StopPromptTimer();

        UpdatePromptMargin();
        PromptText.Text = message;
        PromptText.Visibility = Visibility.Visible;

        if (!autoHide)
        {
            return;
        }

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(PromptDurationMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += PromptTimer_Tick;
        timer.Start();
        _promptTimer = timer;
    }

    private void PromptTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        StopPromptTimer();

        PromptText.Visibility = Visibility.Collapsed;
        PromptText.Text = string.Empty;
    }

    private void StopPromptTimer()
    {
        DispatcherQueueTimer? timer = _promptTimer;
        _promptTimer = null;
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= PromptTimer_Tick;
    }

    private void UpdatePromptMargin()
    {
        double top = 12.0 + (_isOverlayVisible ? _topOverlayHeight : 0.0);
        PromptText.Margin = new Thickness(16.0, top, 16.0, 0);
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
        MediaModel? media = ViewModel.Media;

        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaChanged).Emit(new()
        {
            Media = ViewModel.Media,
            PlayQueue = ViewModel.PlayQueue,
            ImageDescriptions = [],
            PlaybackInfo = _engine?.PlaybackInfo,
        });

        if (media is not null)
        {
            foreach (PluginContext plugin in PluginManager.Instance.GetActivePlugins())
            {
                plugin.SetForegroundMedia(GetMainWindowAbility().PluginWindowContext, media);
            }
        }
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
