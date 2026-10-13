// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
        ViewModel.SetCloseLastTabBehavior(((ComboBox)sender).SelectedIndex);
    }

    private void OpenItemDefaultBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetOpenItemDefaultBehavior(((ComboBox)sender).SelectedIndex);
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
