// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;

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
        ViewModel.SetBackground(((ComboBox)sender).SelectedIndex);
    }

    private void AppearanceRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetAppearance(((RadioButtons)sender).SelectedIndex);
    }
}
