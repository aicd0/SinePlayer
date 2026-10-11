// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.Localization;

namespace SinePlayerUWP.Views.Pages.Player;

internal sealed partial class PlayerNavigationBar : BaseUserControl
{
    public delegate void FavoriteChangedEventHandler(bool isFavorite);
    public event FavoriteChangedEventHandler? FavoriteChanged;

    public delegate void InfoPaneExpandedEventHandler();
    public event InfoPaneExpandedEventHandler? InfoPaneExpanded;

    private bool _isFavorite = false;

    public PlayerNavigationBar()
    {
        InitializeComponent();
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

    private void OnAddToFavoritesClick(object sender, RoutedEventArgs e)
    {
        SetFavorite(!_isFavorite);
    }

    private void OnMediaInfoClick(object sender, RoutedEventArgs e)
    {
        InfoPaneExpanded?.Invoke();
    }
}
