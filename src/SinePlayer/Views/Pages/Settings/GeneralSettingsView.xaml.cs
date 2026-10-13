// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class GeneralSettingsView : BaseUserControl
{
    public GeneralSettingsViewModel ViewModel { get; } = new();

    public GeneralSettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void CloseLastTabBehaviorBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((ComboBox)sender).SelectedIndex;
        List<GeneralSettingsViewModel.CloseLastTabBehaviorEntry> behaviors = ViewModel.CloseLastTabBehaviors;
        if (index < 0 || index >= behaviors.Count)
        {
            return;
        }

        AppSettingsModel.CloseLastTabBehaviorEnum behavior = behaviors[index].Behavior;
        if (AppSettingsModel.CloseLastTabBehavior != behavior)
        {
            AppSettingsModel.CloseLastTabBehavior = behavior;
        }
    }

    private void OpenItemDefaultBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((ComboBox)sender).SelectedIndex;
        List<GeneralSettingsViewModel.TapMediaBehaviorEntry> behaviors = ViewModel.OpenItemDefaultBaheviors;
        if (index < 0 || index >= behaviors.Count)
        {
            return;
        }

        AppSettingsModel.OpenItemBehaviorEnum behavior = behaviors[index].Behavior;
        if (AppSettingsModel.OpenItemDefaultBehavior != behavior)
        {
            AppSettingsModel.OpenItemDefaultBehavior = behavior;
        }
    }

    private void ClearAllHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(() => MediaHistoryItemModel.ClearAsync());
        ViewModel.IsClearHistoryEnabled = false;
    }

    private void SaveBrowsingHistoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.SaveBrowsingHistory = isChecked.Value;
        }
    }

    private void RestoreWindowStateCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.RestoreWindowState = isChecked.Value;
        }
    }
}
