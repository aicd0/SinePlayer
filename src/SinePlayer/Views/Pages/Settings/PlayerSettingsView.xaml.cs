// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class PlayerSettingsView : BaseUserControl
{
    public PlayerSettingsViewModel ViewModel { get; } = new();

    public PlayerSettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void RestoreLastPlaybackPositionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.RestoreLastPlaybackPosition = isChecked.Value;
        }
    }

    private void AutoToggleOverlaysOnCursorCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.AutoToggleOverlaysOnCursor = isChecked.Value;
        }
    }

    private void AutoPlayNextCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.AutoPlayNext = isChecked.Value;
        }
    }

    private void KeepScreenOnBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetKeepScreenOnBehavior(((ComboBox)sender).SelectedIndex);
    }
}
