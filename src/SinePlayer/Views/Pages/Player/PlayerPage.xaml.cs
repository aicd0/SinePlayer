// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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
using SinePlayer.Views.AppWindows.Main;
using SinePlayer.Views.Pages.Main.Sidebar;
using SinePlayer.Views.Pages.Sidebar.MediaInfo;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SinePlayer.Views.Pages.Player;

internal sealed partial class PlayerPage : BasePage
{
    #region Constructor

    private const long SEEK_STEP_MS = 5000;
    private const int VOLUME_STEP = 5;
    private const int SINGLE_TAP_DELAY_MS = 100;
    private const int HIDE_OVERLAY_DELAY_MS = 0;
    private const int HIDE_CURSOR_DELAY_MS = 3000;

    // Can only be accessed on UI thread
    public static IReadOnlyList<Tuple<int, string>> ActiveTabs { get; private set; } = [];

    private readonly PlayerPageViewModel ViewModel = new();

    private PlayerNavigationBar? _navigationBar = null;

    public PlayerPage()
    {
        InitializeComponent();
        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
        AddHandler(PointerPressedEvent, new PointerEventHandler(PlayerPage_AnyPointerPressed), handledEventsToo: true);
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

        bool restored = bundle.GetString(RouterConstants.ARG_RESTORED) == "1";
        string sessionId = bundle.GetString(RouterConstants.ARG_PLAY_QUEUE_ID) ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
            Route route = Route.Create(GetMainPageAbility().Url).WithParam(RouterConstants.ARG_PLAY_QUEUE_ID, sessionId);
            GetMainPageAbility().SetUrl(route.Url);
        }

        string serializedPlayback = bundle.GetString(RouterConstants.ARG_PLAYBACK) ?? string.Empty;
        long initialPositionMs = -1;
        string? positionString = bundle.GetString(RouterConstants.ARG_POSITION);
        if (!string.IsNullOrEmpty(positionString))
        {
            long.TryParse(positionString, CultureInfo.InvariantCulture, out initialPositionMs);
        }

        _restoredFromLastSession = restored;

        _sessionId = sessionId;
        PlayerSession session = EnsureSession();
        _view = session.CreateView();
        ViewModel.Initialize(PageActionHandler, session.Playback);
        ObserveData();
        UpdateKeyboardAccelerators();

        VideoHost.Children.Clear();
        VideoHost.Children.Add(_view.VideoPanel);
        SubtitleHost.Children.Clear();
        SubtitleHost.Children.Add(_view.SubtitlePanel);

        if (_sessionCreator)
        {
            CoroutineUtils.Run(async () =>
            {
                PlayQueueModel playQueue = await GetPlayQueue(sessionId);
                session.Load(playQueue, serializedPlayback, initialPositionMs);
            });
        }
        else
        {
            ViewModel.ShowMedia(session.CurrentMedia);
            ShowState(session.State, session.Error);
            Session_PositionChanged();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        AddToActiveTabs();
        UpdateOverlayStateByMouse();
        UpdatePageState();
        SyncCurrentMedia();
        FocusPlayer();
    }

    protected override void OnPause()
    {
        base.OnPause();
        UpdatePageState();
        ShowCursor();
    }

    protected override void OnStop()
    {
        base.OnStop();
        ViewModel.Destory();
        ReleaseSession();
        StopPromptTimer();
        _navigationBar = null;
        GetMainPageAbility().SetCustomCenteredNavigationBar(null);
        ShowCursor();
        RemoveFromActiveTabs();
    }

    private void InitializeNavigationBar()
    {
        var bar = new PlayerNavigationBar();
        bar.Initialize(GetMainWindowAbility());
        bar.FavoriteChanged += isFavorite =>
        {
            ViewModel.SetFavorite(isFavorite);
            _session?.SetFavorite(isFavorite);
        };
        bar.MediaInfoRequested += OpenMediaInfoPane;
        bar.PlaybackInfoRequested += ShowPlaybackInfo;
        bar.PlayerSettingsChanged += settings => CoroutineUtils.Run(() => ApplyPlayerSettings(settings));
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
            UpdateSubtitlePresentation();

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
            UpdateSubtitlePresentation();

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
            _isMouseInsideRootElement = isInside;
            UpdateOverlayStateByMouse();
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

            MediaModel? media = _session?.CurrentMedia;
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
            TransportBar.SetLoading(status.Status == PlayerPageViewModel.PlayerStatusEnum.Loading);
            if (status.Status == PlayerPageViewModel.PlayerStatusEnum.Error)
            {
                PromptForever(status.Description);
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

        ViewModel.VolumeLiveData.ObserveSticky(this, TransportBar.SetVolume);

        ViewModel.MuteLiveData.ObserveSticky(this, TransportBar.SetMuted);
    }

    private async Task<PlayQueueModel> GetPlayQueue(string playQueueId)
    {
        string playQueuesRegistry = $"{RegistryNames.TAB_RESOURCES}/{GetMainPageAbility().TabId}/PlayQueues";

        PlayQueueModel? playQueue = null;
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

        playQueue ??= PlayQueueModel.CreateEmpty();
        AppDB.MainRegistry.CreateKey(playQueuesRegistry).Set(playQueueId, playQueue.ToSerializedString());
        return playQueue;
    }

    #endregion

    #region Common events

    private void VideoGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
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
            PlayerSession? session = _session;
            if (settingsPanel is null || media is null || session is null)
            {
                return;
            }

            PlayerMenuContext context = new()
            {
                WindowId = WindowId,
                ActionHandler = PageActionHandler,
                Media = media,
                Settings = settingsPanel.Settings,
                UpdateSettings = settingsPanel.Apply,
                PlayQueue = session.PlayQueue.ToBuilder(),
                Playback = ViewModel.Playback.ToBuilder(),
                Session = session,
                View = _view,
                Url = GetMainPageAbility().Url,
                PromptInfo = PromptTemporary,
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
        UpdateSubtitlePresentation();

        if (ViewModel.IsPinned)
        {
            UpdatePinRelatedUI();
        }
    }

    #endregion

    #region Drag-and-drop

    private void VideoGrid_Drop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral deferral = args.GetDeferral();
        args.Handled = true;
        CoroutineUtils.Run(async () =>
        {
            try
            {
                await HandleDropAsync(args);
            }
            catch (Exception exception)
            {
                Logger.W(TAG, $"What was dropped could not be read: {exception.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    private async Task HandleDropAsync(DragEventArgs args)
    {
        IReadOnlyList<IStorageItem> items = await args.DataView.GetStorageItemsAsync();

        List<string> subtitles = [];
        List<string> others = [];
        foreach (IStorageItem item in items)
        {
            if (item is not StorageFile file || string.IsNullOrEmpty(file.Path))
            {
                Logger.W(TAG, $"'{item.Name}' is not a file that can be reached by its path, so it was ignored.");
                continue;
            }

            if (AppInfoProvider.IsSupportedSubtitleExtension(Path.GetExtension(file.Path)))
            {
                subtitles.Add(file.Path);
            }
            else
            {
                others.Add(file.Path);
            }
        }

        if (subtitles.Count > 0)
        {
            await AddSubtitleFilesAsync(subtitles);
        }

        if (others.Count == 0)
        {
            return;
        }

        MainWindow? window = App.Instance.WindowManager.GetWindow(WindowId);
        if (window is null)
        {
            Logger.W(TAG, $"The window with ID '{WindowId}' could not be found, so the dropped files were not opened.");
            return;
        }

        await App.Instance.OnCommandLine(window, [.. others]);
    }

    private async Task AddSubtitleFilesAsync(IReadOnlyList<string> paths)
    {
        PlayerSession? session = _session;
        PlayerView? view = _view;
        MediaModel? media = ViewModel.Media;
        if (session is null || view is null || media is null)
        {
            PromptTemporary("There is no playback to add the subtitles to.");
            return;
        }

        if (ViewModel.PlayerStatus == PlayerPageViewModel.PlayerStatusEnum.Loading)
        {
            Logger.W(TAG, "Subtitles were dropped while the player was loading, so they were not added.");
            return;
        }

        OperationResult added = await SubtitleSourceLoader.AddAndSelect(session, view, media, paths);
        if (!added.IsSuccessful)
        {
            PromptTemporary(added.Message);
        }
    }

    #endregion

    #region Session

    private const string TAG = nameof(PlayerPage);

    private string _sessionId = string.Empty;
    private bool _sessionCreator;
    private PlayerSession? _session;
    private PlayerView? _view;
    private SubtitlePresentation _subtitlePresentation = SubtitlePresentation.Identity;

    /// <summary>
    /// The playback moved to a media, and the session has opened it. This page's part is what only it can do: the media
    /// it shows, the picture and the words its own view draws, and whether the playback starts. What the media records
    /// about being played, and what comes after it ends, are the session's. The work here is this page's own, so a
    /// media that has already been replaced by the time an await returns is left alone.
    /// </summary>
    private void Session_MediaChanged(MediaModel? media, OperationResult opened)
    {
        CoroutineUtils.Run(async () =>
        {
            PlayerSession? session = _session;
            PlayerView? view = _view;
            if (session is null || view is null)
            {
                return;
            }

            ViewModel.PositionLiveData.Emit(0);
            ViewModel.ShowMedia(media);

            if (media is null)
            {
                return;
            }

            if (session.CurrentMedia != media)
            {
                return;
            }

            if (!opened.IsSuccessful)
            {
                ViewModel.PlayerStatusLiveData.Emit(new()
                {
                    Status = PlayerPageViewModel.PlayerStatusEnum.Error,
                    Description = opened.Message,
                });
                return;
            }

            ViewModel.PlayerStatusLiveData.Emit(new() { Status = PlayerPageViewModel.PlayerStatusEnum.Loading });

            await ApplyPlayerSettings(PlayerSettingsModel.LoadFromMedia(media));

            OperationResult played = await SelectTracks(session, view, media);
            if (played.IsSuccessful)
            {
                ViewModel.PlayerStatusLiveData.Emit(new() { Status = PlayerPageViewModel.PlayerStatusEnum.Working });
            }
            else
            {
                ViewModel.PlayerStatusLiveData.Emit(new()
                {
                    Status = PlayerPageViewModel.PlayerStatusEnum.Error,
                    Description = played.Message,
                });
            }

            if (session.StartPositionMs > 0)
            {
                SeekMode mode = AppSettingsModel.PreciseSeeking ? SeekMode.Precise : SeekMode.Fast;
                session.Seek(TimeSpan.FromMilliseconds(session.StartPositionMs), mode);
            }

            StartPlaybackOnOpen(session);

            ViewModel.SetDuration(session.Duration.Ticks / TimeSpan.TicksPerMillisecond);
            ViewModel.PositionLiveData.Emit(session.Position.Ticks / TimeSpan.TicksPerMillisecond);
        });
    }

    private async Task<OperationResult> SelectTracks(PlayerSession session, PlayerView view, MediaModel media)
    {
        List<string> unplayable = [];

        MediaStreamInfo? video = TrackSelection.Resolve(media, MediaStreamKind.Video, session.Sources);
        if (video is not null)
        {
            OperationResult chosen = await session.SelectVideoStream(view, video);
            if (!chosen.IsSuccessful)
            {
                unplayable.Add($"the picture could not be played ({chosen.Message})");
            }
        }

        MediaStreamInfo? subtitle = TrackSelection.Resolve(media, MediaStreamKind.Subtitle, session.Sources);
        if (subtitle is not null)
        {
            OperationResult chosen = await session.SelectSubtitleStream(view, subtitle);
            if (!chosen.IsSuccessful)
            {
                unplayable.Add($"the subtitles could not be played ({chosen.Message})");
            }
        }

        bool playing = false;
        OperationResult<ReconcileResult> reconciled = await session.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            PromptTemporary(reconciled.Message);
        }
        else
        {
            ReconcileResult result = reconciled.Result;

            if (video is not null)
            {
                OperationResult? videoResult = result.Video(view);
                playing |= videoResult?.IsSuccessful == true;
                if (videoResult is { IsSuccessful: false })
                {
                    PromptTemporary(videoResult.Message);
                }
            }

            playing |= result.Audio()?.IsSuccessful == true;

            if (subtitle is not null && result.Subtitle(view) is { IsSuccessful: false } subtitleFailure)
            {
                PromptTemporary(subtitleFailure.Message);
            }
        }

        if (!playing)
        {
            string why = unplayable.Count == 0
                ? "neither the picture nor the sound could be played"
                : string.Join(", ", unplayable);
            return OperationResultExt.Failure(TAG, $"Unable to play media file '{media.Location}': {why}.");
        }

        if (unplayable.Count > 0)
        {
            Logger.W(TAG, $"Playing '{media.Location}' without everything it holds: {string.Join(", ", unplayable)}.");
        }

        return OperationResult.Success();
    }

    private void StartPlaybackOnOpen(PlayerSession session)
    {
        bool restoredFromLastSession = _restoredFromLastSession;
        _restoredFromLastSession = false;

        if (!AppSettingsModel.AutoPlayOnOpen || (restoredFromLastSession && AppSettingsModel.AutoPlayOnOpenExceptAppLaunch))
        {
            return;
        }

        if (IsResumed)
        {
            session.Play();
        }
        else
        {
            _playbackPendingOnActivate = true;
        }
    }

    private PlayerSession EnsureSession()
    {
        if (_session is not null)
        {
            return _session;
        }

        PlayerSession session = PlayerSessionManager.Shared.Acquire(_sessionId, out _sessionCreator);
        if (_sessionCreator)
        {
            session.SetVolume(Math.Clamp(AppSettingsModel.PlayerVolume, 0.0, 1.0));
            session.SetMuted(AppSettingsModel.PlayerMuted);
        }

        session.StateChanged += Session_StateChanged;
        session.PositionChanged += Session_PositionChanged;
        session.MediaChanged += Session_MediaChanged;
        _session = session;
        UpdateSubtitlePresentation();

        ViewModel.VolumeLiveData.Emit((int)Math.Round(session.Volume * 100.0));
        ViewModel.MuteLiveData.Emit(session.IsMuted);
        return session;
    }

    private void ReleaseSession()
    {
        PlayerSession? session = _session;
        PlayerView? view = _view;
        _session = null;
        _view = null;
        if (session is null)
        {
            return;
        }

        if (view is not null)
        {
            session.RemoveView(view);
        }

        session.StateChanged -= Session_StateChanged;
        session.PositionChanged -= Session_PositionChanged;
        session.MediaChanged -= Session_MediaChanged;
        PlayerSessionManager.Shared.Release(session.Id);
    }

    private void Session_StateChanged(MediaEngineState state, string? message)
    {
        ShowState(state, message);

        if (state == MediaEngineState.Playing)
        {
            UnholdOverlay();
        }
    }

    private void Session_PositionChanged()
    {
        PlayerSession? session = _session;
        if (session is null)
        {
            return;
        }

        ViewModel.SetPosition(
            session.Position.Ticks / TimeSpan.TicksPerMillisecond,
            session.Duration.Ticks / TimeSpan.TicksPerMillisecond);
    }

    private void ShowState(MediaEngineState state, string? message)
    {
        ViewModel.IsPlayingLiveData.Emit(state == MediaEngineState.Playing);
        ViewModel.IsPlaying = state == MediaEngineState.Playing;

        if (state == MediaEngineState.Error)
        {
            ViewModel.PlayerStatusLiveData.Emit(new()
            {
                Status = PlayerPageViewModel.PlayerStatusEnum.Error,
                Description = message ?? "Unknown error",
            });
        }

        UpdateKeepScreenOn();
    }

    #endregion

    #region Gesture

    private readonly PlayerGestureRecognizer _gestureRecognizer = new();
    private bool _tapPending = false;
    private bool _tapCancelled = false;

    private void VideoGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        FocusPlayer();
        ((UIElement)sender).CapturePointer(e.Pointer);
        _gestureRecognizer.ProcessDownEvent(e.GetCurrentPoint(VideoHost));
    }

    private void VideoGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _gestureRecognizer.ProcessMoveEvents(e.GetIntermediatePoints(VideoHost));

        // A move reported at the position the pointer already had is not the user moving it: a frame presented on the
        // surface underneath sends one too. Restarting the wait for those is what makes the cursor hide late, so only a
        // real movement counts.
        Windows.Foundation.Point position = e.GetCurrentPoint(VideoHost).Position;
        if (_lastPointerPosition == position)
        {
            return;
        }

        _lastPointerPosition = position;
        KeepCursorVisible();
    }

    private void VideoGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _gestureRecognizer.ProcessUpEvent(e.GetCurrentPoint(VideoHost));
    }

    private void VideoGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
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

                ToggleOverlayHold();
            });
        }
        else if (e.TapCount == 2)
        {
            _tapCancelled = true;
            TogglePlayPause();
        }
    }

    private void PlayerPage_AnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            return;
        }

        bool IsElementInsideVideo(object? source)
        {
            for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element == VideoHost)
                {
                    return true;
                }
            }

            return false;
        }

        _isPointerInsideOverlay = !IsElementInsideVideo(e.OriginalSource);
    }

    private void VideoGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
        {
            return;
        }

        _isMouseInsideVideo = true;
        UpdateOverlayStateByMouse();

        _lastPointerPosition = null;
        KeepCursorVisible();
    }

    private void VideoGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
        {
            return;
        }

        _isMouseInsideVideo = false;
        UpdateOverlayStateByMouse();

        _lastPointerPosition = null;
        ShowCursor();
    }

    #endregion

    #region Overlay

    private bool _isPointerInsideOverlay = false;
    private bool _isMouseInsideRootElement = true;
    private bool _isMouseInsideVideo = false;

    private double _topOverlayHeight = 0.0;
    private double _rightOverlayWidth = 0.0;
    private double _bottomGridHeight = 0.0;
    private bool _isOverlayVisible = false;
    private bool _isOverlayHold = false;
    private long _hideOverlaySession = 0;
    private long _hideOverlayDeadline = -1;

    private void UpdateOverlayStateByMouse()
    {
        bool GetMouseInsideWindowState()
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

        bool isMouseInsideWindow = GetMouseInsideWindowState();
        bool isPointerInsideOverlay = (isMouseInsideWindow || _isMouseInsideRootElement) && !_isMouseInsideVideo;

        if (isPointerInsideOverlay == _isPointerInsideOverlay)
        {
            return;
        }

        _isPointerInsideOverlay = isPointerInsideOverlay;

        if (AppSettingsModel.AutoToggleOverlaysOnCursor)
        {
            if (isPointerInsideOverlay)
            {
                ShowOverlay();
            }
            else
            {
                HideOverlay(delay: HIDE_OVERLAY_DELAY_MS);
            }
        }
    }

    private void ToggleOverlayHold()
    {
        _isOverlayHold = !_isOverlayHold;
        if (_isOverlayHold)
        {
            ShowOverlay();
        }
        else
        {
            HideOverlay();
        }
    }

    private void UnholdOverlay()
    {
        _isOverlayHold = false;

        PointerDeviceType pointerDeviceType = GetMainWindowAbility().LastPointerDeviceType;
        if (pointerDeviceType == PointerDeviceType.Mouse || !_isPointerInsideOverlay)
        {
            HideOverlay();
        }
        else
        {
            HideOverlay(delay: 3000, ignorePointerPosition: true);
        }
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
        UpdateSubtitlePresentation();
        GetMainPageAbility().SetOverlayVisibility(true);
    }

    private void HideOverlay(int delay = 0, bool ignorePointerPosition = false)
    {
        bool CanHideOverlay()
        {
            return _isOverlayVisible &&
                !ViewModel.IsPinned &&
                (ignorePointerPosition || !_isPointerInsideOverlay) &&
                !_isOverlayHold;
        }

        if (!CanHideOverlay())
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            long session = ++_hideOverlaySession;

            if (delay > 0)
            {
                _hideOverlayDeadline = GetTick() + delay;

                while (true)
                {
                    long remaining = _hideOverlayDeadline - GetTick();
                    if (remaining <= 0)
                    {
                        if (!CanHideOverlay())
                        {
                            return;
                        }

                        break;
                    }

                    await Task.Delay((int)remaining + 1);

                    if (session != _hideOverlaySession || _hideOverlayDeadline == -1)
                    {
                        return;
                    }
                }
            }

            HideOverlayInternal();
        });
    }

    private void HideOverlayInternal()
    {
        _hideOverlayDeadline = -1;

        if (!_isOverlayVisible)
        {
            return;
        }

        _isOverlayVisible = false;
        _isOverlayHold = false;
        UpdatePromptMargin();
        UpdateSubtitlePresentation();
        GetMainPageAbility().SetOverlayVisibility(false);
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
        PlayerSession? session = _session;
        if (session is null)
        {
            return;
        }

        if (session.State == MediaEngineState.Playing)
        {
            session.Pause();
        }
        else
        {
            session.Play();
        }
    }

    private void SeekBy(long deltaMs)
    {
        PlayerSession? session = _session;
        if (session is null)
        {
            return;
        }

        long positionMs = session.Position.Ticks / TimeSpan.TicksPerMillisecond;
        SeekTo(Math.Max(positionMs + deltaMs, 0));
    }

    private void SeekTo(long positionMs)
    {
        SeekMode mode = AppSettingsModel.PreciseSeeking ? SeekMode.Precise : SeekMode.Fast;
        _session?.Seek(TimeSpan.FromMilliseconds(positionMs), mode);
    }

    private void SetVolume(int volume)
    {
        PlayerSession? session = _session;
        if (session is null)
        {
            return;
        }

        int clampedVolume = Math.Clamp(volume, 0, 100);
        session.SetVolume(clampedVolume / 100.0);
        ViewModel.SetVolume(clampedVolume);
    }

    private void AdjustVolume(int delta)
    {
        SetVolume(ViewModel.VolumeLiveData.Value + delta);
    }

    private void SetMuted(bool isMuted)
    {
        PlayerSession? session = _session;
        if (session is null)
        {
            return;
        }

        session.SetMuted(isMuted);
        ViewModel.SetMute(isMuted);
    }

    private async Task ApplyPlayerSettings(PlayerSettingsModel settings)
    {
        PlayerSession? session = _session;
        PlayerView? view = _view;
        if (session is null || view is null)
        {
            return;
        }

        view.VideoTransform = settings.ToVideoTransform();

        _subtitlePresentation = _subtitlePresentation with
        {
            OffsetX = settings.SubtitlePanX,
            OffsetY = settings.SubtitlePanY,
            FontFamily = settings.SubtitleFontFamily,
            FontSize = settings.SubtitleFontSize,
            FontStyle = settings.ToFontStyle(),
            FontWeight = settings.ToFontWeight(),
            OutlineWidth = settings.SubtitleOutlineWidth,
        };
        session.SetSubtitlePresentation(_subtitlePresentation);

        PlaybackComponents components = settings.ToComponents();
        session.SetComponents(components);

        if (session.State == MediaEngineState.None)
        {
            return;
        }

        OperationResult<ReconcileResult> reconciled = await session.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            PromptTemporary(reconciled.Message);
            return;
        }

        ReconcileResult result = reconciled.Result;

        if (result.Video(view) is { IsSuccessful: false } videoFailure)
        {
            PromptTemporary(videoFailure.Message);
        }

        if (result.Audio() is { IsSuccessful: false } audioFailure)
        {
            PromptTemporary(audioFailure.Message);
        }

        if (result.Subtitle(view) is { IsSuccessful: false } subtitleFailure)
        {
            PromptTemporary(subtitleFailure.Message);
        }
    }

    #endregion

    #region Cursor

    private bool _isCursorHidden = false;
    private long _hideCursorDeadline = -1;
    private bool _waitingToHideCursor = false;
    private Windows.Foundation.Point? _lastPointerPosition = null;

    private void KeepCursorVisible()
    {
        ShowCursor();
        _hideCursorDeadline = Environment.TickCount64 + HIDE_CURSOR_DELAY_MS;

        if (_waitingToHideCursor)
        {
            return;
        }

        _waitingToHideCursor = true;
        CoroutineUtils.Run(async () =>
        {
            while (true)
            {
                long deadline = _hideCursorDeadline;
                if (deadline == -1)
                {
                    _waitingToHideCursor = false;
                    return;
                }

                long remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    break;
                }

                await Task.Delay((int)remainingMs);
            }

            _waitingToHideCursor = false;
            HideCursor();
        });
    }

    private void ShowCursor()
    {
        _hideCursorDeadline = -1;

        if (!_isCursorHidden)
        {
            return;
        }

        _isCursorHidden = false;
        ProtectedCursor = null;
    }

    private void HideCursor()
    {
        _hideCursorDeadline = -1;

        if (_isCursorHidden)
        {
            return;
        }

        _isCursorHidden = true;

        InputCursor cursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        cursor.Dispose();
        ProtectedCursor = cursor;
    }

    #endregion

    #region Player prompt

    private const int PromptDurationMilliseconds = 3000;

    private DispatcherQueueTimer? _promptTimer;

    private void PromptTemporary(string message)
    {
        ShowPrompt(message, autoHide: true);
    }

    private void PromptForever(string message)
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

    private void UpdateSubtitlePresentation()
    {
        SubtitleInsets insets = _isOverlayVisible
            ? new SubtitleInsets(0.0, _topOverlayHeight, _rightOverlayWidth, _bottomGridHeight)
            : SubtitleInsets.None;
        _subtitlePresentation = _subtitlePresentation with { Insets = insets };
        _session?.SetSubtitlePresentation(_subtitlePresentation);
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
            if (AppSettingsModel.PauseOnLeaveTab && !ViewModel.IsAudio)
            {
                _session?.Pause();
            }

            ViewModel.Next();
        }

        if (IsResumed && _playbackPendingOnActivate)
        {
            _playbackPendingOnActivate = false;
            _session?.Play();
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
            AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlaying => _session?.State == MediaEngineState.Playing,
            AppSettingsModel.KeepScreenOnBehaviorEnum.WhilePlayingVideos => _session?.State == MediaEngineState.Playing && media is not null && media.Type == MediaType.Video,
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
        PlayerSession? session = _session;

        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaChanged).Emit(new()
        {
            Media = ViewModel.Media,
            PlayQueue = session?.PlayQueue ?? PlayQueueModel.CreateEmpty(),
            ImageDescriptions = [],
            PlaybackInfo = _view?.PlaybackInfo,
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
