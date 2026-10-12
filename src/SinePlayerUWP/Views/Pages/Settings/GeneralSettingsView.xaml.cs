// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Data.Models.Misc;

namespace SinePlayerUWP.Views.Pages.Settings;

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

    private void OpenMediaDefaultBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetOpenMediaDefaultBehavior(((ComboBox)sender).SelectedIndex);
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
}
