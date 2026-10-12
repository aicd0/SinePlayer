// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.Views.Dialogs.ChooseLocation;

namespace SinePlayer.Views.Pages.Settings;

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

        MediaModel.IsScanningLibraryLiveData.ObserveSticky(owner, isScanning =>
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
            ViewModel.SetRemoveUnreachableMedia(isChecked.Value);
        }
    }

    private void PromptBeforeRemovingMediaCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            ViewModel.SetPromptBeforeRemovingMedia(isChecked.Value);
        }
    }

    private void OnRescanFilesClicked(object sender, RoutedEventArgs e)
    {
        MediaModel.RescanLibrary("OnRescanFilesClicked");
    }

    private void EditImportExclusionListButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            ErrorResult err = await MediaImportExclusionModel.Instance.EditWithNotepad();
            err.DisplayErrorMessage(ViewModel.Shared.ActionHandler);
        });

    }

    private void ShowHiddenMediaButton_Click(object sender, RoutedEventArgs e)
    {
        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_SEARCH)
            .WithParam(RouterConstants.ARG_KEYWORD, $"{MediaSQLProviderUtils.VAR_HIDDEN}:1");
        ActionModel action = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
            .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
            .Build();
        ViewModel.Shared.ActionHandler.HandleNoResult(action);
    }
}
