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

    private void ZoomInNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.ZoomInStep, decimals: 2);
        if (AppSettingsModel.ZoomInStep != value)
        {
            AppSettingsModel.ZoomInStep = value;
        }
    }

    private void ZoomOutNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.ZoomOutStep, decimals: 2);
        if (AppSettingsModel.ZoomOutStep != value)
        {
            AppSettingsModel.ZoomOutStep = value;
        }
    }

    private void FastForwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.FastForwardSpeed, decimals: 2);
        if (AppSettingsModel.FastForwardSpeed != value)
        {
            AppSettingsModel.FastForwardSpeed = value;
        }
    }

    private void SyncVideoForwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncVideoForwardStep, decimals: 1);
        if (AppSettingsModel.SyncVideoForwardStep != value)
        {
            AppSettingsModel.SyncVideoForwardStep = value;
        }
    }

    private void SyncVideoBackwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncVideoBackwardStep, decimals: 1);
        if (AppSettingsModel.SyncVideoBackwardStep != value)
        {
            AppSettingsModel.SyncVideoBackwardStep = value;
        }
    }

    private void SyncAudioForwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncAudioForwardStep, decimals: 1);
        if (AppSettingsModel.SyncAudioForwardStep != value)
        {
            AppSettingsModel.SyncAudioForwardStep = value;
        }
    }

    private void SyncAudioBackwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncAudioBackwardStep, decimals: 1);
        if (AppSettingsModel.SyncAudioBackwardStep != value)
        {
            AppSettingsModel.SyncAudioBackwardStep = value;
        }
    }

    private void SyncSubtitleForwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncSubtitleForwardStep, decimals: 1);
        if (AppSettingsModel.SyncSubtitleForwardStep != value)
        {
            AppSettingsModel.SyncSubtitleForwardStep = value;
        }
    }

    private void SyncSubtitleBackwardNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SyncSubtitleBackwardStep, decimals: 1);
        if (AppSettingsModel.SyncSubtitleBackwardStep != value)
        {
            AppSettingsModel.SyncSubtitleBackwardStep = value;
        }
    }

    private void IncreaseSubtitleFontSizeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.IncreaseSubtitleFontSizeStep);
        if (AppSettingsModel.IncreaseSubtitleFontSizeStep != value)
        {
            AppSettingsModel.IncreaseSubtitleFontSizeStep = value;
        }
    }

    private void DecreaseSubtitleFontSizeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.DecreaseSubtitleFontSizeStep);
        if (AppSettingsModel.DecreaseSubtitleFontSizeStep != value)
        {
            AppSettingsModel.DecreaseSubtitleFontSizeStep = value;
        }
    }

    private void VideoPanStepNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.VideoPanStep);
        if (AppSettingsModel.VideoPanStep != value)
        {
            AppSettingsModel.VideoPanStep = value;
        }
    }

    private void SubtitlePanStepNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        double value = ReadStep(sender, AppSettingsModel.SubtitlePanStep);
        if (AppSettingsModel.SubtitlePanStep != value)
        {
            AppSettingsModel.SubtitlePanStep = value;
        }
    }

    private void ReadAheadNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        int value = (int)ReadStep(sender, ViewModel.ReadAheadSeconds);
        ViewModel.SetReadAheadSeconds(value);
    }

    private void FrameQueueCapacityNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        int value = (int)ReadStep(sender, AppSettingsModel.FrameQueueCapacity);
        if (AppSettingsModel.FrameQueueCapacity != value)
        {
            AppSettingsModel.FrameQueueCapacity = value;
        }
    }

    private void SubtitleMemoryCacheCapacityNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        int value = (int)ReadStep(sender, ViewModel.SubtitleMemoryCacheCapacity);
        ViewModel.SetSubtitleMemoryCacheCapacity(value);
    }

    private static double ReadStep(NumberBox box, double fallback, int decimals = 0)
    {
        if (double.IsNaN(box.Value))
        {
            box.Value = fallback;
            return fallback;
        }

        double value = Math.Round(Math.Clamp(box.Value, box.Minimum, box.Maximum), decimals);
        if (box.Value != value)
        {
            box.Value = value;
        }

        return value;
    }
}
