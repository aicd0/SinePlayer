// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.HotKey;
using SinePlayer.UserControls.Misc;

namespace SinePlayer.Views.Dialogs.EditKeyboardShortcut;

internal sealed partial class EditKeyboardShortcutDialog : BaseContentDialog
{
    public EditKeyboardShortcutDialogViewModel ViewModel { get; } = new();

    public EditKeyboardShortcutDialog(KeyboardShortcutModel? original)
    {
        InitializeComponent();

        ViewModel.Initialize(original);

        if (original is not null)
        {
            Recorder.SetShortcut(original);
            ViewModel.UpdateShortcut(original);
        }

        Recorder.ShortcutChanged += OnRecorderShortcutChanged;
        Opened += OnDialogOpened;
    }

    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Recorder.Focus(FocusState.Programmatic);
    }

    private void OnRecorderShortcutChanged(KeyboardShortcutRecorder sender, KeyboardShortcutModel model)
    {
        ViewModel.UpdateShortcut(model);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CreateResult() is not null)
        {
            Hide();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.ActionIndex = ((ComboBox)sender).SelectedIndex;
    }
}
