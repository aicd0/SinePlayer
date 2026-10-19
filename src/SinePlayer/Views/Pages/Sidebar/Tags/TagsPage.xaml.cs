// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Misc;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Views.Dialogs.EditTag;
using SinePlayer.Views.Dialogs.EditTagCategory;

namespace SinePlayer.Views.Pages.Sidebar.Tags;

internal sealed partial class TagsPage : BasePage
{
    private const string TAG = nameof(TagsPage);

    private readonly TagsPageViewModel ViewModel = new();

    public TagsPage()
    {
        InitializeComponent();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        PageActionHandler.RegisterProvider(new CustomActionProvider(new CustomActionHandler(ViewModel)));

        ObserveData();
        ViewModel.Initialize(PageActionHandler);
        ViewModel.UpdateTags();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.TagInfoUpdated.Observe(this, _ =>
        {
            ViewModel.UpdateTags();
        });

        GlobalEvent.Instance.MediaUpdated.Observe(this, _ =>
        {
            ViewModel.UpdateTags();
        });

        ViewModel.EditTagCategoryLiveData.Observe(this, tagCategory =>
        {
            var dialog = new EditTagCateogoryDialog(tagCategory);
            CoroutineUtils.Run(() => dialog.ShowAsync(WindowId));
        });

        ViewModel.EditTagLiveData.Observe(this, pair =>
        {
            var dialog = new EditTagDialog(pair.Key, pair.Value);
            CoroutineUtils.Run(() => dialog.ShowAsync(WindowId));
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

    //
    // Types
    //

    private class CustomActionHandler(TagsPageViewModel viewModel) : CustomActionProvider.IHandler
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
