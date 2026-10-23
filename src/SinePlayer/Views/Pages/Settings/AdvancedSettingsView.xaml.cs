// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Storage;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;
using SinePlayer.SDK.Models;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class AdvancedSettingsView : BaseUserControl
{
    public AdvancedSettingsViewModel ViewModel { get; } = new();

    public AdvancedSettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void DebugModeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool debugMode = ((ToggleSwitch)sender).IsOn;
        if (ViewModel.Shared.DebugMode == debugMode)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            if (debugMode)
            {
                DialogOptions options = new DialogOptions.Builder()
                    .SetTitle(StringResourceProvider.Instance.Warning)
                    .SetContent(StringResourceProvider.Instance.DebugModeWarning)
                    .SetPrimaryButtonText(StringResourceProvider.Instance.Proceed)
                    .SetCloseButtonText(StringResourceProvider.Instance.Cancel)
                    .Build();
                DialogResult result = await DialogUtils.EnqueueDialogAsync(ViewModel.Shared.WindowId, options);
                if (result != DialogResult.Primary)
                {
                    ViewModel.Shared.DebugMode = false;
                    return;
                }
            }

            ViewModel.Shared.DebugMode = debugMode;
            DebugUtils.DebugMode = debugMode;
        });
    }

    private void DeveloperModeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool developerMode = ((ToggleSwitch)sender).IsOn;
        if (ViewModel.DeveloperMode == developerMode)
        {
            return;
        }

        ViewModel.DeveloperMode = developerMode;
        DebugUtils.DeveloperMode = developerMode;
    }

    private void SendUsageDataToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        bool sendUsageData = ((ToggleSwitch)sender).IsOn;
        AppSettingsModel.SendUsageData = sendUsageData;
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCache();
    }

    private void OnRefreshRandomSeedClick(object sender, RoutedEventArgs e)
    {
        AppSettingsModel.MediaShuffleRandomSeed = Random.Shared.Next();
    }

    private async void OnOpenUserDataFolderClick(object sender, RoutedEventArgs e)
    {
        ErrorResult err = await ErrorLogger.Run(nameof(OnOpenUserDataFolderClick), async err =>
        {
            string path = StorageLocation.LocalFolderPath;
            try
            {
                Windows.Storage.StorageFolder folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
                await Windows.System.Launcher.LaunchFolderAsync(folder);
            }
            catch (Exception ex)
            {
                return err.Error(ex);
            }

            return err.Success();
        });

        err.ShowErrorMessage(ViewModel.Shared.ActionHandler);
    }

    private void ResetAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            DialogOptions options = new DialogOptions.Builder()
                .SetTitle(StringResourceProvider.Instance.ResetAllSettings)
                .SetContent(StringResourceProvider.Instance.ResetAllSettingsMessage)
                .SetPrimaryButtonText(StringResourceProvider.Instance.Proceed)
                .SetCloseButtonText(StringResourceProvider.Instance.Cancel)
                .Build();
            DialogResult result = await DialogUtils.EnqueueDialogAsync(ViewModel.Shared.WindowId, options);
            if (result == DialogResult.Primary)
            {
                AppSettingsModel.Reset();
                ViewModel.Shared.Update();
            }
        });
    }
}
