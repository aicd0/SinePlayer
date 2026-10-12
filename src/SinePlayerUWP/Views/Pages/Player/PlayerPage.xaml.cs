// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.BaseUI.PageAbilities;
using SinePlayerUWP.Common.Constants;
using SinePlayerUWP.Common.HotKey;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Database;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Data.Models.Playback;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;
using SinePlayerUWP.Helpers.Navigation;
using SinePlayerUWP.Views.Pages.Main.Sidebar;
using SinePlayerUWP.Views.Pages.Sidebar.MediaInfo;

namespace SinePlayerUWP.Views.Pages.Player;

internal sealed partial class PlayerPage : BasePage
{
    private const long SEEK_STEP_MS = 10000;
    private const double VOLUME_STEP = 0.05;

    // Can only be accessed by UI thread
    public static IReadOnlyList<Tuple<int, string>> ActiveTabs { get; private set; } = [];

    private readonly PlayerPageViewModel ViewModel = new();

    private string _loadedKey = string.Empty;
    private PlayerNavigationBar? _navigationBar = null;
    private bool _isFullscreenHandlerRegistered = false;

    // Overlay
    private bool _shouldOverlayVisible = false;
    private bool _isPointerInsideRootElement = true;
    private bool _isPointerInsideVideo = false;
    private double _topOverlayHeight = 0.0;
    private double _rightOverlayWidth = 0.0;
    private double _bottomGridHeight = 0.0;
    private bool _isOverlayVisible = false;
    private bool _isOverlayHold = false;
    private long _hideOverlayDeadline = -1;

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

        SetupNavigationBar();
        SetupTransportBar();
        SetupFullscreenHandler();

        {
            bool pinned = AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValueOrDefault(RegistryNames.StateKey.PLAYER_OVERLAY_PINNED, false);
            ViewModel.SetPinned(pinned);
            TransportBar.SetPinned(pinned);
            UpdatePinRelatedUI();
        }

        ViewModel.Initialize(PageActionHandler);
        ObserveData();
        UpdateKeyboardAccelerators();

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
        UpdateOverlayState();
        ViewModel.SetPageActive(true);
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
        _loadedKey = string.Empty;
        _navigationBar = null;
        GetMainPageAbility().SetCustomCenteredNavigationBar(null);
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

            if (status.Status == PlayerPageViewModel.PlayerStatusEnum.Error)
            {
                ShowOverlay();
            }
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.IsPlayingLiveData.Observe(this, isPlaying =>
        {
            TransportBar.SetPlaying(isPlaying);
            if (!isPlaying)
            {
                ShowOverlay();
            }
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.PositionLiveData.Observe(this, positionMs =>
        {
            TransportBar.SetPosition(positionMs, ViewModel.DurationMs);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.IsFavoriteLiveData.Observe(this, isFavorite =>
        {
            _navigationBar?.SetFavorite(isFavorite);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.IsExternalMediaLiveData.Observe(this, isExternal =>
        {
            _navigationBar?.SetExternalMedia(isExternal);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.PlaybackChangeLiveData.Observe(this, _ =>
        {
            TransportBar.SetNavigation(ViewModel.IsPlaybackPreviousEnabled, ViewModel.IsPlaybackNextEnabled);
            GetWindowEventBus().With<PlaybackModel>(EventId.PlaybackChanged).Emit(ViewModel.Playback);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.MediaChangedLiveData.Observe(this, _ =>
        {
            if (ViewModel.Media is not null)
            {
                _navigationBar?.SetPlayerSettings(ViewModel.Media);
            }

            GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaInfoChanged).Emit(new()
            {
                Media = ViewModel.Media,
                Playlist = ViewModel.Playlist,
                ImageDescriptions = [],
            });
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

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

        GetMainWindowAbility().RegisterPointerInsideRootElementChangedHandler(this, isInside =>
        {
            _isPointerInsideRootElement = isInside;
            UpdateOverlayState();
        });

        KeyboardShortcutManager.ShortcutsChangedLiveData.Observe(this, _ => UpdateKeyboardAccelerators());

        ViewModel.VolumeLiveData.Observe(this, volume =>
        {
            TransportBar.SetVolume(volume);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        ViewModel.MuteLiveData.Observe(this, isMuted =>
        {
            TransportBar.SetMuted(isMuted);
        }, new ObserveOptions()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ActiveOnStart,
        });

        AppSettingsModel.KeepScreenOnBehaviorChangedLiveData.Observe(this, _ => ViewModel.RefreshKeepScreenOn());
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
    // Setup
    //

    private void SetupNavigationBar()
    {
        var bar = new PlayerNavigationBar();
        bar.Initialize(GetMainWindowAbility());
        bar.FavoriteChanged += isFavorite => ViewModel.SetIsFavorite(isFavorite, true);
        bar.InfoPaneExpanded += OpenMediaInfoPane;
        GetMainPageAbility().SetCustomCenteredNavigationBar(bar);
        _navigationBar = bar;
    }

    private void SetupTransportBar()
    {
        TransportBar.PlayPauseRequested += ViewModel.TogglePlayPause;
        TransportBar.PreviousRequested += ViewModel.Previous;
        TransportBar.NextRequested += ViewModel.Next;
        TransportBar.PlaylistRequested += OpenPlaylistPane;
        TransportBar.MoreRequested += ShowPlaybackMoreMenu;
        TransportBar.SeekRequested += ViewModel.SeekTo;
        TransportBar.VolumeChanged += ViewModel.SetVolume;
        TransportBar.MuteChanged += ViewModel.SetMuted;
        TransportBar.PinRequested += TogglePin;
        TransportBar.FullscreenRequested += ToggleFullscreen;
        TransportBar.SizeChanged += TransportBar_SizeChanged;
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

    private void TogglePin()
    {
        ViewModel.SetPinned(!ViewModel.IsPinned);
        TransportBar.SetPinned(ViewModel.IsPinned);
        UpdatePinRelatedUI();
        AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.PLAYER_OVERLAY_PINNED, ViewModel.IsPinned);
    }

    private void OpenPlaylistPane()
    {
        GetMainPageAbility().SetSidePanePage(SidebarView.ITEM_PLAYLIST);
        GetMainPageAbility().SetSidePaneOpenState(true, force: true);
    }

    private void ShowPlaybackMoreMenu()
    {
        List<BaseMenuFlyoutItemModel> menuItems = CreatePlaybackMoreMenuItems();
        if (menuItems.Count == 0)
        {
            return;
        }

        MenuFlyout flyout = new()
        {
            Placement = FlyoutPlacementMode.Top,
        };
        foreach (BaseMenuFlyoutItemModel item in menuItems)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        flyout.ShowAt(TransportBar.MoreButton);
    }

    private List<BaseMenuFlyoutItemModel> CreatePlaybackMoreMenuItems()
    {
        List<BaseMenuFlyoutItemModel> items = [];

        {
            bool repeat = ViewModel.Playback.IsRepeat;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Repeat,
                IsChecked = repeat,
                Click = () =>
                {
                    ViewModel.Playback.IsRepeat = !repeat;
                },
            });
        }

        {
            bool shuffle = ViewModel.Playback.IsShuffle;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Shuffle,
                IsChecked = shuffle,
                Click = () =>
                {
                    ViewModel.Playback.IsShuffle = !shuffle;
                },
            });
        }

        return items;
    }

    private void SetupFullscreenHandler()
    {
        if (_isFullscreenHandlerRegistered)
        {
            return;
        }

        _isFullscreenHandlerRegistered = true;
        GetMainWindowAbility().RegisterFullscreenChangedHandler(this, isFullscreen =>
        {
            ViewModel.SetFullscreen(isFullscreen);
            TransportBar.SetFullscreen(isFullscreen);
        });
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

    private void OpenMediaInfoPane()
    {
        GetWindowEventBus().With<MediaChangedEventArgs>(EventId.MediaInfoChanged).Emit(new()
        {
            Media = ViewModel.Media,
            Playlist = ViewModel.Playlist,
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

    //
    // Overlay
    //

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
                TryHideOverlay(1000);
            }
        }
    }

    private void TryHideOverlay(int delayMilliseconds = 0)
    {
        if (!_isOverlayVisible ||
            ViewModel.IsPinned ||
            _isOverlayHold ||
            _shouldOverlayVisible ||
            !ViewModel.IsAutoPlaying)
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

    private void BottomTileSetHold(bool hold)
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
    // Events
    //

    private void VideoHost_Tapped(object sender, TappedRoutedEventArgs e)
    {
        BottomTileSetHold(!_isOverlayVisible);
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

    //
    // Utilities
    //

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
