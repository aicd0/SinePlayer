// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Localization;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.UserControls.PlayerSettings;

namespace SinePlayer.Views.Pages.Player;

internal sealed partial class PlayerNavigationBar : BaseUserControl
{
    public delegate void FavoriteChangedEventHandler(bool isFavorite);
    public event FavoriteChangedEventHandler? FavoriteChanged;

    public delegate void MediaInfoRequestedEventHandler();
    public event MediaInfoRequestedEventHandler? MediaInfoRequested;

    public delegate void PlaybackInfoRequestedEventHandler();
    public event PlaybackInfoRequestedEventHandler? PlaybackInfoRequested;

    public delegate void PlayerSettingsChangedEventHandler(PlayerSettingsModel settings);
    public event PlayerSettingsChangedEventHandler? PlayerSettingsChanged;

    private IMainWindowAbility? _windowAbility;
    private bool _isFavorite = false;
    private bool _isPlayerSettingFlyoutActive = false;

    public PlayerNavigationBar()
    {
        InitializeComponent();
    }

    public PlayerSettingsPanel PlayerSettingsPanel => MainPlayerSettingPanel;

    public void Initialize(IMainWindowAbility windowAbility)
    {
        _windowAbility = windowAbility;
        MainPlayerSettingPanel.Initialize(windowAbility);
    }

    public void SetFavorite(bool isFavorite)
    {
        _isFavorite = isFavorite;
        FiFavoriteFilled.Visibility = isFavorite ? Visibility.Visible : Visibility.Collapsed;
        FiFavoriteUnfilled.Visibility = isFavorite ? Visibility.Collapsed : Visibility.Visible;
        string toolTip = isFavorite ? StringResourceProvider.Instance.RemoveFromFavorites :
            StringResourceProvider.Instance.AddToFavorites;
        ToolTipService.SetToolTip(AbbAddToFavorite, toolTip);
        FavoriteChanged?.Invoke(isFavorite);
    }

    public void SetExternalMedia(bool isExternalMedia)
    {
        AbbAddToFavorite.IsEnabled = !isExternalMedia;
    }

    public void SetPlayerSettings(MediaModel media)
    {
        MainPlayerSettingPanel.SetMedia(media);
    }

    private void OnAddToFavoritesClick(object sender, RoutedEventArgs e)
    {
        SetFavorite(!_isFavorite);
    }

    private void MediaInfoButton_Click(object sender, RoutedEventArgs e)
    {
        MediaInfoRequested?.Invoke();
    }

    private void PlaybackInfoButton_Click(object sender, RoutedEventArgs e)
    {
        PlaybackInfoRequested?.Invoke();
    }

    private void PlayerSettingFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        args.Cancel = MainPlayerSettingPanel.ActionInProgress;
    }

    private void PlayerSettingFlyout_Opened(object sender, object e)
    {
        IMainWindowAbility? windowAbility = _windowAbility;
        if (windowAbility is null)
        {
            return;
        }

        if (!_isPlayerSettingFlyoutActive)
        {
            windowAbility.RequestFocusLock();
            _isPlayerSettingFlyoutActive = true;
        }
    }

    private void PlayerSettingFlyout_Closed(object sender, object e)
    {
        IMainWindowAbility? windowAbility = _windowAbility;
        if (windowAbility is null)
        {
            return;
        }

        if (_isPlayerSettingFlyoutActive)
        {
            windowAbility.ReleaseFocusLock();
            _isPlayerSettingFlyoutActive = false;
        }
    }

    private void MainPlayerSettingPanel_DataChanged(PlayerSettingsModel model)
    {
        PlayerSettingsChanged?.Invoke(model);
    }
}
