// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.Views.Pages.Settings;

internal sealed partial class AppearanceSettingsView : BaseUserControl
{
    public AppearanceSettingsViewModel ViewModel { get; } = new();

    public AppearanceSettingsView()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
    }

    private void BackgroundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((ComboBox)sender).SelectedIndex;
        List<SettingsPageViewModel.BackgroundEntry> backgrounds = ViewModel.Backgrounds;
        if (index < 0 || index >= backgrounds.Count)
        {
            return;
        }

        AppSettingsModel.AppBackgroundEnum background = backgrounds[index].Value;
        if (AppSettingsModel.AppBackground != background)
        {
            AppSettingsModel.AppBackground = background;
            ViewModel.AppearanceChanged = true;
        }
    }

    private void AppearanceRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = ((RadioButtons)sender).SelectedIndex;
        if (index < 0)
        {
            return;
        }

        AppSettingsModel.AppearanceSetting appearance = index switch
        {
            0 => AppSettingsModel.AppearanceSetting.Light,
            1 => AppSettingsModel.AppearanceSetting.Dark,
            _ => AppSettingsModel.AppearanceSetting.UseSystemSetting,
        };
        if (AppSettingsModel.Theme != appearance)
        {
            AppSettingsModel.Theme = appearance;
            ViewModel.AppearanceChanged = true;
        }
    }
}
