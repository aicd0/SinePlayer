// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Constants;
using SinePlayer.Common.HotKey;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.SDK.Models;
using SinePlayer.Views.Pages.Main.Sidebar;
using SinePlayer.Views.Pages.Sidebar.MediaInfo;

namespace SinePlayer.Views.Pages.Player;

internal sealed partial class PlayerPage : BasePage
{
    private const long SEEK_STEP_MS = 5000;
    private const int VOLUME_STEP = 5;
    private const int SINGLE_TAP_DELAY_MS = 100;
    private const int HIDE_OVERLAY_DELAY_MS = 0;

    // Can only be accessed by UI thread
    public static IReadOnlyList<Tuple<int, string>> ActiveTabs { get; private set; } = [];

    private readonly PlayerPageViewModel ViewModel = new();

    private PlayerNavigationBar? _navigationBar = null;

    //
    // Constructor
    //

    public PlayerPage()
    {
        InitializeComponent();

        _gestureRecognizer.Tapped += GestureRecognizer_Tapped;
    }

    //
    // Lifecycle
    //

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);
        AddToActiveTabs();

        InitializeNavigationBar();
        InitializeTransportBar();
        InitializePinState();

        ViewModel.Initialize(PageActionHandler);
        ObserveData();
        UpdateKeyboardAccelerators();

        string playQueueId = bundle.GetString(RouterConstants.ARG_PLAY_QUEUE_ID) ?? string.Empty;
        string serializedPlayback = bundle.GetString(RouterConstants.ARG_PLAYBACK) ?? string.Empty;
        long initialPositionMs = -1;
        string? positionString = bundle.GetString(RouterConstants.ARG_POSITION);
        if (!string.IsNullOrEmpty(positionString))
        {
            long.TryParse(positionString, CultureInfo.InvariantCulture, out initialPositionMs);
        }

        VideoHost.Children.Clear();
        VideoHost.Children.Add(ViewModel.GetVideoSurface());

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
        ViewModel.SetPageActive(true);
        FocusPlayer();
    }

    protected override void OnPause()
    {
        base.OnPause();
        ViewModel.Pause();
        ViewModel.SetPageActive(false);
    }

    protected override void OnStop()
    {
        base.OnStop();
        ViewModel.Destory();
        _navigationBar = null;
        GetMainPageAbility().SetCustomCenteredNavigationBar(null);
        RemoveFromActiveTabs();
    }

    private void InitializeNavigationBar()
    {
        var bar = new PlayerNavigationBar();
        bar.Initialize(GetMainWindowAbility());
        bar.FavoriteChanged += isFavorite => ViewModel.SetIsFavorite(isFavorite, true);
        bar.InfoPaneExpanded += OpenMediaInfoPane;
        GetMainPageAbility().SetCustomCenteredNavigationBar(bar);
        _navigationBar = bar;
    }

    private void InitializeTransportBar()
    {
        TransportBar.PlayPauseRequested += ViewModel.TogglePlayPause;
        TransportBar.PreviousRequested += ViewModel.Previous;
        TransportBar.NextRequested += ViewModel.Next;
        TransportBar.PlayQueueRequested += OpenPlayQueuePane;
        TransportBar.RepeatRequested += ViewModel.CycleRepeatMode;
        TransportBar.ShuffleRequested += ViewModel.ToggleShuffle;
        TransportBar.SeekRequested += ViewModel.SeekTo;
        TransportBar.VolumeChanged += ViewModel.SetVolume;
        TransportBar.MuteChanged += ViewModel.SetMuted;
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
        AppSettingsModel.KeepScreenOnBehaviorChangedLiveData.Observe(this, _ => ViewModel.RefreshKeepScreenOn());

        KeyboardShortcutManager.ShortcutsChangedLiveData.Observe(this, _ => UpdateKeyboardAccelerators());

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

            GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaInfoChanged).Emit(new()
            {
                Media = media,
                PlayQueue = ViewModel.PlayQueue,
                ImageDescriptions = [],
            });
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

    //
    // Events
    //

    private void VideoHost_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        args.Handled = true;

        CoroutineUtils.Run(async () =>
        {
            IReadOnlyList<BaseMenuFlyoutItemModel> menuItems = await ViewModel.CreateMediaContextMenuItems();

            MenuFlyout flyout = new();
            foreach (BaseMenuFlyoutItemModel item in menuItems)
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

    //
    // Gesture
    //

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
            ViewModel.TogglePlayPause();
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

    //
    // Overlay
    //

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

    //
    // Misc
    //

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
        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaInfoChanged).Emit(new()
        {
            Media = ViewModel.Media,
            PlayQueue = ViewModel.PlayQueue,
            ImageDescriptions = [],
        });
        GetMainPageAbility().SetSidePanePage(SidebarView.ITEM_MEDIA_INFO);
        GetMainPageAbility().SetSidePaneOpenState(true, force: true);
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

        AddKeyboardAccelerators(KeyboardShortcutActions.PlayPause, (_, _) => ViewModel.TogglePlayPause());
        AddKeyboardAccelerators(KeyboardShortcutActions.SeekBackward, (_, _) => ViewModel.SeekBy(-SEEK_STEP_MS));
        AddKeyboardAccelerators(KeyboardShortcutActions.SeekForward, (_, _) => ViewModel.SeekBy(SEEK_STEP_MS));
        AddKeyboardAccelerators(KeyboardShortcutActions.VolumeDown, (_, _) => ViewModel.AdjustVolume(-VOLUME_STEP));
        AddKeyboardAccelerators(KeyboardShortcutActions.VolumeUp, (_, _) => ViewModel.AdjustVolume(VOLUME_STEP));
        AddKeyboardAccelerators(KeyboardShortcutActions.OpenNextItem, (_, _) => ViewModel.Next());
        AddKeyboardAccelerators(KeyboardShortcutActions.OpenPreviousItem, (_, _) => ViewModel.Previous());
    }

    //
    // Utilities
    //

    private void FocusPlayer()
    {
        Focus(FocusState.Programmatic);
    }

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
}
