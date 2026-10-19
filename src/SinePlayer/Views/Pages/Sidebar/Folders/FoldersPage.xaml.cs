// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Misc;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Helpers.MenuFlyoutHelpers;

namespace SinePlayer.Views.Pages.Sidebar.Folders;

internal sealed partial class FoldersPage : BasePage
{
    private const string TAG = nameof(FoldersPage);

    private readonly FoldersPageViewModel ViewModel = new();

    public FoldersPage()
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
    }

    //
    // Utilities
    //

    private IMainPageAbility GetMainPageAbility()
    {
        return GetAbility<IMainPageAbility>()!;
    }

    //
    // Events
    //

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ((TextBox)sender).Text;
        ViewModel.SetSearchText(text);
    }

    //
    // Types
    //

    private class CustomActionHandler(FoldersPageViewModel viewModel) : CustomActionProvider.IHandler
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
