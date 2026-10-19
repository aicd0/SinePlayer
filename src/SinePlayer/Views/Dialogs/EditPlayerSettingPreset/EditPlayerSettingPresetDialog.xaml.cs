// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.Views.Dialogs.EditPlayerSettingPreset;

internal sealed partial class EditPlayerSettingPresetDialog : BaseContentDialog
{
    public EditPlayerSettingPresetDialogViewModel ViewModel = new();

    public EditPlayerSettingPresetDialog(MediaModel media)
    {
        InitializeComponent();

        ViewModel.Initialize(media);
    }

    //
    // Events
    //

    private void CancelButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Hide();
    }

    private void DeleteButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.Delete();
        Hide();
    }

    private void SaveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.Save();
        Hide();
    }

    private void SaveAsNewButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.SaveAsNew();
        Hide();
    }

    private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var textBox = (TextBox)sender;
        ViewModel.UpdateName(textBox.Text ?? string.Empty);
    }

    private void SetAsDefaultCheckBox_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var checkBox = (CheckBox)sender;
        ViewModel.UpdateSetAsDefault(checkBox.IsChecked ?? false);
    }
}
