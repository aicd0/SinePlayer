// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Misc;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Helpers.MenuFlyoutHelpers;

namespace SinePlayer.Views.Pages.Sidebar.FilterPresets;

internal sealed partial class FilterPresetsPage : BasePage
{
    private const string TAG = nameof(FilterPresetsPage);

    private readonly FilterPresetsPageViewModel ViewModel = new();

    public FilterPresetsPage()
    {
        InitializeComponent();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        PageActionHandler.RegisterProvider(new CustomActionProvider(new CustomActionHandler(ViewModel)));

        ObserveData();
        ViewModel.Initialize(PageActionHandler);
        ViewModel.UpdateMedia();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.MediaUpdated.Observe(this, _ =>
        {
            ViewModel.UpdateMedia();
        });

        GlobalEvent.Instance.FilterUpdated.Observe(this, _ =>
        {
            ViewModel.UpdateMedia();
        });

        ViewModel.FilterPresetDropDownLiveData.ObserveSticky(this, model =>
        {
            FilterPresetDropDownTextBlock.Text = model.Name;

            FlyoutBase flyout = FilterPresetDropDownButton.Flyout;
            if (flyout is not MenuFlyout)
            {
                flyout = new MenuFlyout
                {
                    Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
                };
                FilterPresetDropDownButton.Flyout = flyout;
            }

            var menuFlyout = (MenuFlyout)flyout;
            menuFlyout.Items.Clear();
            foreach (BaseMenuFlyoutItemModel item in model.Items)
            {
                menuFlyout.Items.Add(item.CreateMenuFlyoutItem());
            }
        });
    }

    //
    // Events
    //

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ((TextBox)sender).Text;
        ViewModel.SetSearchText(text);
    }

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        List<MediaModel> snapshot = ViewModel.GetMediaSnapshot();
        List<BaseMenuFlyoutItemModel> menuItems = await MenuFlyoutItemsCreator.CreateMediaGroupMenuItems(
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
    }

    //
    // Types
    //

    private class CustomActionHandler(FilterPresetsPageViewModel viewModel) : CustomActionProvider.IHandler
    {
        public void Handle(string source, string name, IReadOnlyList<string> args)
        {
            bool handled = true;
            switch (source)
            {
                case MenuFlyoutItemsCreator.CUSTOM_ACTION_SOURCE_MEDIA_ITEM_MENU:
                    switch (name)
                    {
                        case MenuFlyoutItemsCreator.CUSTOM_ACTION_NAME_SELECT:
                            viewModel.SelectionMode = !viewModel.SelectionMode;
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
