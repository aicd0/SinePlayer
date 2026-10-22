// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;

using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Navigation;
using SinePlayer.UserControls.Misc;
using SinePlayer.ViewModels;
using SinePlayer.Views.Dialogs.EditFilter;

using Windows.Storage;

namespace SinePlayer.Views.Pages.Home;

internal sealed partial class HomePage : BasePage
{
    private const string TAG = nameof(HomePage);

    private readonly HomePageViewModel ViewModel = new();

    private readonly SearchNavigationBar _searchNavigationBar;

    private MediaFilterModel.ViewTypeEnum? _viewType = null;
    private Storyboard? _headerTextBlockAnimation = null;
    private double _lastGridViewVerticalOffset = 0.0;

    public HomePage()
    {
        InitializeComponent();
        _searchNavigationBar = new();
    }

    //
    // Lifecycle
    //

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        PageActionHandler.RegisterProvider(new CustomActionProvider(new CustomActionHandler(ViewModel)));
        ItemsView.Initialize(ViewModel.MediaSelection, PageActionHandler);
        ViewTypeSelector.ViewTypeChanged += ViewModel.SelectViewType;

        GetMainPageAbility().SetTitle(StringResourceProvider.Instance.NewTab);
        GetMainPageAbility().SetIcon(new SymbolIconSource() { Symbol = Symbol.Document });
        GetMainPageAbility().SetCustomNavigationBar(_searchNavigationBar);

        ViewModel.Initialize(PageActionHandler, bundle.GetString(RouterConstants.ARG_FILTER_JSON));

        ObserveData();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.MediaUpdated.Observe(this, _ =>
        {
            ViewModel.Refresh(filters: true, library: true);
        });

        GlobalEvent.Instance.FavoriteUpdated.Observe(this, _ =>
        {
            ViewModel.Refresh(library: true);
        });

        GlobalEvent.Instance.FilterUpdated.Observe(this, _ =>
        {
            ViewModel.Refresh(filters: true);
        });

        GetMainPageAbility().RegisterRefreshHandler(this, () =>
        {
            MediaModel.RescanLibrary("HomePage#RefreshPage");
        });

        ViewModel.UrlLiveData.ObserveSticky(this, url =>
        {
            GetMainPageAbility().SetUrl(url);
        });

        ViewModel.FilterLiveData.ObserveSticky(this, UpdateFilters);

        ViewModel.GroupingEnabledLiveData.ObserveSticky(this, grouped =>
        {
            if (grouped)
            {
                ItemsView.SetGroupedItems(ViewModel.GroupedMediaItems);
            }
            else
            {
                ItemsView.SetItems(ViewModel.UngroupedMediaItems);
            }
        });

        ViewModel.ViewTypeLiveData.ObserveSticky(this, type =>
        {
            ViewTypeSelector.SelectedViewType = type;

            if (_viewType == type)
            {
                return;
            }

            _viewType = type;
            ItemsView.ItemViewType = type;
        });

        _searchNavigationBar.SearchTextChange += ViewModel.SetSearchText;

        _searchNavigationBar.SearchTextSubmitted += text =>
        {
            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_SEARCH)
                .WithParam(RouterConstants.ARG_KEYWORD, text);
            GetMainPageAbility().OpenInCurrentTab(route);
        };
    }

    //
    // Grid View
    //

    private void ItemsView_ScrollOffsetChanged(double verticalOffset)
    {
        {
            Thickness p = HeaderAreaGrid.Padding;
            double newTop = Math.Max(20 - verticalOffset, 6);
            if (p.Top != newTop)
            {
                p.Top = newTop;
                HeaderAreaGrid.Padding = p;
            }
        }

        {
            double newOpacity = Math.Min(1.0, Math.Max(0, verticalOffset - 10) * 0.05);
            if (HeaderAreaBackgroundGrid.Opacity != newOpacity)
            {
                HeaderAreaBackgroundGrid.Opacity = newOpacity;
            }
        }

        if (verticalOffset > 20 && _lastGridViewVerticalOffset <= 20)
        {
            AnimateHeaderTextBlockOpacity(0.0);
        }
        else if (verticalOffset < 20 && _lastGridViewVerticalOffset >= 20)
        {
            AnimateHeaderTextBlockOpacity(1.0);
        }

        _lastGridViewVerticalOffset = verticalOffset;
    }

    private void ItemsView_GroupCollapseRequested(MediaGroupViewModel group)
    {
        ViewModel.CollapseOrExpandGroup(group);
    }

    //
    // Animation
    //

    private void AnimateHeaderTextBlockOpacity(double to)
    {
        double from = HeaderTextBlock.Opacity;
        if (_headerTextBlockAnimation != null)
        {
            _headerTextBlockAnimation.Stop();
            _headerTextBlockAnimation = null;
        }
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(Math.Abs(to - from) * 0.2)),
        };
        Storyboard.SetTarget(animation, HeaderTextBlock);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
        _headerTextBlockAnimation = storyboard;
    }

    //
    // Filters
    //

    private void UpdateFilters(HomePageViewModel.FilterModel model)
    {
        if (model == null)
        {
            return;
        }

        BindDropDownButton(SortAndGroupDropDownButton, SortAndGroupDropDownButtonText, model.SortAndGroupDropDown);
        BindDropDownButton(FilterPresetDropDownButton, FilterPresetDropDownButtonText, model.FilterPresetDropDown);
    }

    private void BindDropDownButton(DropDownButton button, TextBlock buttonText, HomePageViewModel.DropDownButtonModel model)
    {
        if (button == null)
        {
            return;
        }

        buttonText.Text = model.Name;

        FlyoutBase flyout = button.Flyout;
        if (flyout is not MenuFlyout)
        {
            flyout = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
            };
            button.Flyout = flyout;
        }

        var menuFlyout = (MenuFlyout)flyout;

        menuFlyout.Items.Clear();
        foreach (BaseMenuFlyoutItemModel item in model.Items)
        {
            menuFlyout.Items.Add(item.CreateMenuFlyoutItem());
        }
    }

    //
    // Utilities
    //

    private IMainPageAbilityForTab GetMainPageAbility()
    {
        return GetAbility<IMainPageAbilityForTab>()!;
    }

    //
    // Media Item
    //

    private void EditFilterButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            var dialog = new EditFilterDialog(await ViewModel.GetFilter());
            await dialog.ShowAsync(WindowId);
            if (dialog.HasMadeChanges)
            {
                ViewModel.Refresh(clearFilter: true);
            }
        });
    }

    private void AddNewFolder()
    {
        CoroutineUtils.Run(async () =>
        {
            StorageFolder? folder = await FilePickerUtils.PickFolder(WindowId);
            if (folder == null)
            {
                return;
            }

            AppSettingsModel.AddMediaFolder(folder.Path);
            MediaModel.RescanLibrary("HomePage#AddNewFolder");
        });
    }

    private void AddFolderHyperlink_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        AddNewFolder();
    }

    private void RefreshHyperlink_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        MediaModel.RescanLibrary("RefreshPage");
    }

    //
    // More actions
    //

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            if (sender is not FrameworkElement fe)
            {
                return;
            }

            IReadOnlyList<MediaModel> snapshot = ViewModel.GetMedia();
            List<BaseMenuFlyoutItemModel> menuItems = await MediaMenuCreator.CreateMediaGroupMenuItems(
                PageActionHandler, snapshot, ViewModel.ExpandAllGroups, ViewModel.CollapseAllGroups);
            if (menuItems.Count == 0)
            {
                return;
            }

            MenuFlyout flyout = new()
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            };
            foreach (BaseMenuFlyoutItemModel item in menuItems)
            {
                flyout.Items.Add(item.CreateMenuFlyoutItem());
            }

            flyout.ShowAt(fe);
        });
    }

    private class CustomActionHandler(HomePageViewModel viewModel) : CustomActionProvider.IHandler
    {
        private const string TAG = nameof(CustomActionHandler);

        public void Handle(string source, string name, IReadOnlyList<string> args)
        {
            bool handled = true;
            switch (source)
            {
                case MediaMenuCreator.CUSTOM_ACTION_SOURCE_MEDIA_ITEM_MENU:
                    switch (name)
                    {
                        case MediaMenuCreator.CUSTOM_ACTION_NAME_SELECT:
                            viewModel.MediaSelection.SetSelectMode(!viewModel.MediaSelection.IsSelectMode);
                            break;
                        default:
                            handled = false;
                            break;
                    }
                    break;
                default:
                    handled = false;
                    break;
            }

            if (!handled)
            {
                Logger.F(TAG, $"Unknown action '{source}.{name}'");
            }
        }
    }
}
