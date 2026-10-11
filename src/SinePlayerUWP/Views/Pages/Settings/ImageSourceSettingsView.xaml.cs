// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.Actions.Providers;
using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.ErrorHandling;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Comic;
using SinePlayerUWP.Data.Models.Misc;
using SinePlayerUWP.Helpers.Navigation;
using SinePlayerUWP.Helpers.Search;
using SinePlayerUWP.Views.Dialogs.ChooseLocation;

namespace SinePlayerUWP.Views.Pages.Settings;

internal sealed partial class ImageSourceSettingsView : BaseUserControl
{
    public ImageSourceSettingsViewModel ViewModel { get; } = new();

    public ImageSourceSettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(ILifecycleOwner owner, SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);

        ComicModel.IsScanningLibraryLiveData.ObserveSticky(owner, isScanning =>
        {
            ViewModel.IsRescanning = isScanning;
        });
    }

    private void ChooseLocationsClick(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            var dialog = new ChooseLocationsDialog();
            await dialog.ShowAsync(ViewModel.Shared.WindowId);
        });
    }

    private void ScanOnLaunchCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            ViewModel.SetScanOnLaunch(isChecked.Value);
        }
    }

    private void RemoveUnreachableCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            ViewModel.SetRemoveUnreachableComics(isChecked.Value);
        }
    }

    private void PromptBeforeRemovingComicsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            ViewModel.SetPromptBeforeRemovingComics(isChecked.Value);
        }
    }

    private void OnRescanFilesClicked(object sender, RoutedEventArgs e)
    {
        ComicModel.RescanLibrary("OnRescanFilesClicked");
    }

    private void EditImportExclusionListButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            ErrorResult err = await ComicImportExclusionModel.Instance.EditWithNotepad();
            err.DisplayErrorMessage(ViewModel.Shared.ActionHandler);
        });

    }

    private void ShowHiddenComicButton_Click(object sender, RoutedEventArgs e)
    {
        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_SEARCH)
            .WithParam(RouterConstants.ARG_KEYWORD, $"{ComicSQLProviderUtils.VAR_HIDDEN}:1");
        ActionModel action = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
            .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
            .Build();
        ViewModel.Shared.ActionHandler.HandleNoResult(action);
    }
}
