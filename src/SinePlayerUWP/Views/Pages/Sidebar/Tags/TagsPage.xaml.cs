// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.Actions.Providers;
using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.Misc;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Helpers.MenuFlyoutHelpers;
using SinePlayerUWP.Views.Dialogs.EditTag;
using SinePlayerUWP.Views.Dialogs.EditTagCategory;

namespace SinePlayerUWP.Views.Pages.Sidebar.Tags;

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

        GlobalEvent.Instance.ComicUpdated.Observe(this, _ =>
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
                case MenuFlyoutItemsCreator.CUSTOM_ACTION_SOURCE_COMIC_ITEM_MENU:
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
