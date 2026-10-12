// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.BaseUI.PageAbilities;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Models.Misc;

namespace SinePlayerUWP.Views.Pages.Player;

internal sealed partial class PlayerNavigationBar : BaseUserControl
{
    public delegate void FavoriteChangedEventHandler(bool isFavorite);
    public event FavoriteChangedEventHandler? FavoriteChanged;

    public delegate void InfoPaneExpandedEventHandler();
    public event InfoPaneExpandedEventHandler? InfoPaneExpanded;

    public delegate void PlayerSettingsChangedEventHandler(PlayerSettingsModel settings);
    public event PlayerSettingsChangedEventHandler? PlayerSettingsChanged;

    private IMainWindowAbility? _windowAbility;
    private bool _isFavorite = false;
    private bool _isPlayerSettingFlyoutActive = false;

    public PlayerNavigationBar()
    {
        InitializeComponent();
    }

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

    private void OnMediaInfoClick(object sender, RoutedEventArgs e)
    {
        InfoPaneExpanded?.Invoke();
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
