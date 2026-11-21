// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
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

    private void AutoPlayOnOpenExceptAppLaunchCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.AutoPlayOnOpenExceptAppLaunch = isChecked.Value;
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

    private void PreciseSeekingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool? isChecked = ((CheckBox)sender).IsChecked;
        if (isChecked.HasValue)
        {
            AppSettingsModel.PreciseSeeking = isChecked.Value;
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

    private void SeekForwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SeekForwardSeconds);
        if (AppSettingsModel.SeekForwardSeconds != value)
        {
            AppSettingsModel.SeekForwardSeconds = value;
        }
    }

    private void SeekBackwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SeekBackwardSeconds);
        if (AppSettingsModel.SeekBackwardSeconds != value)
        {
            AppSettingsModel.SeekBackwardSeconds = value;
        }
    }

    private void VolumeUpNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.VolumeUpPercent);
        if (AppSettingsModel.VolumeUpPercent != value)
        {
            AppSettingsModel.VolumeUpPercent = value;
        }
    }

    private void VolumeDownNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.VolumeDownPercent);
        if (AppSettingsModel.VolumeDownPercent != value)
        {
            AppSettingsModel.VolumeDownPercent = value;
        }
    }

    private static double ReadStep(NumberBox box, double fallback)
    {
        if (double.IsNaN(box.Value))
        {
            box.Value = fallback;
            return fallback;
        }

        double value = Math.Round(Math.Clamp(box.Value, box.Minimum, box.Maximum));
        if (box.Value != value)
        {
            box.Value = value;
        }

        return value;
    }
}
