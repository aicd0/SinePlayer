// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

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

    private void AutoPlayOnOpenCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.AutoPlayOnOpen = isChecked.Value;
        }
    }

    private void PauseOnLeaveTabCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.PauseOnLeaveTab = isChecked.Value;
        }
    }

    private void KeepScreenOnBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((ComboBox)sender).SelectedIndex;
        List<PlayerSettingsViewModel.KeepScreenOnBehaviorEntry> behaviors = ViewModel.KeepScreenOnBehaviors;
        if (index < 0 || index >= behaviors.Count)
        {
            return;
        }

        AppSettingsModel.KeepScreenOnBehaviorEnum behavior = behaviors[index].Behavior;
        if (AppSettingsModel.KeepScreenOnBehavior != behavior)
        {
            AppSettingsModel.KeepScreenOnBehavior = behavior;
        }
    }
}
