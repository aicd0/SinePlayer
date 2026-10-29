// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Constants;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Plugins;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Lifecycle;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Database;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.SDK.Models;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class PluginSettingsView : BaseUserControl
{
    public PluginSettingsViewModel ViewModel { get; } = new();

    public PluginSettingsView()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            Bindings.StopTracking();
            Bindings.Update();
        };
    }

    protected override void OnResume()
    {
        base.OnResume();
        ObserveData();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void ObserveData()
    {
        ObserveOptions options = new()
        {
            Sticky = true,
            PublishBehavior = LiveDataPublishBehavior.ResumeOnly,
        };

        PluginManager.PluginsChanged.Observe(this, _ =>
        {
            ViewModel.UpdatePlugins();
        }, options);
    }

    private void OpenPluginsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            OperationResult result = await OpenPluginsFolder();
            result.ShowErrorMessage(ViewModel.Shared.ActionHandler);
        });
    }

    private static async Task<OperationResult> OpenPluginsFolder()
    {
        string path = PluginManager.PluginsFolderPath;
        try
        {
            Windows.Storage.StorageFolder folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(nameof(OpenPluginsFolder), ex);
        }

        return OperationResult.Success();
    }

    private void InstallPluginButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            Windows.Storage.StorageFile? file = await FilePickerUtils.PickFile(ViewModel.Shared.WindowId, [".zip"]);
            if (file is null)
            {
                return;
            }

            if (AppDB.MainRegistry.CreateKey(RegistryNames.STATE).GetValueOrDefault(RegistryNames.StateKey.SHOW_INSTALL_PLUGIN_WARNING, true))
            {
                DialogOptions options = new DialogOptions.Builder()
                    .SetTitle(StringResourceProvider.Instance.Warning)
                    .SetContent(StringResourceProvider.Instance.InstallPluginWarning)
                    .SetPrimaryButtonText(StringResourceProvider.Instance.Proceed)
                    .SetSecondaryButtonText(StringResourceProvider.Instance.Cancel)
                    .Build();
                DialogResult result = await DialogUtils.EnqueueDialogAsync(ViewModel.Shared.WindowId, options);
                if (result != DialogResult.Primary)
                {
                    return;
                }

                AppDB.MainRegistry.CreateKey(RegistryNames.STATE).Set(RegistryNames.StateKey.SHOW_INSTALL_PLUGIN_WARNING, false);
            }

            string pluginsFolderPath = PluginManager.PluginsFolderPath;
            string dstFilePath = Path.Combine(pluginsFolderPath, file.Name);

            OperationResult installed = InstallPlugin(file.Path, dstFilePath);

            if (installed.IsSuccessful)
            {
                ViewModel.ApplyOnNextLaunchVisible = true;
            }

            installed.ShowErrorMessage(ViewModel.Shared.ActionHandler);
        });
    }

    private static OperationResult InstallPlugin(string sourcePath, string destinationPath)
    {
        try
        {
            File.Copy(sourcePath, destinationPath, true);
        }
        catch (Exception ex)
        {
            return OperationResultExt.Failure(nameof(InstallPlugin), ex);
        }

        return OperationResult.Success();
    }

    private void DownloadPluginsFromGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () => await Windows.System.Launcher.LaunchUriAsync(new Uri(StaticStringResources.GITHUB_PLUGINS_REPO_URL)));
    }

    private void PluginItemMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        if (fe.DataContext is not PluginItemViewModel item)
        {
            return;
        }

        List<BaseMenuFlyoutItemModel> menuItems = item.CreateOperationMenuItems();
        if (menuItems.Count == 0)
        {
            return;
        }

        var flyout = new MenuFlyout()
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };
        foreach (BaseMenuFlyoutItemModel menuItem in menuItems)
        {
            flyout.Items.Add(menuItem.CreateMenuFlyoutItem());
        }

        flyout.ShowAt(fe);
    }
}
