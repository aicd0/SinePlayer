// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.HotKey;
using SinePlayerUWP.Core.Common.Lifecycle;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Views.Dialogs.EditKeyboardShortcut;

namespace SinePlayerUWP.Views.Pages.Settings;

internal sealed partial class KeyboardShortcutSettingsView : BaseUserControl
{
    public KeyboardShortcutSettingsView()
    {
        InitializeComponent();
    }

    public KeyboardShortcutSettingsViewModel ViewModel { get; } = new();

    public void Initialize(ILifecycleOwner owner, SettingsSharedViewModel shared)
    {
        ViewModel.Initialize(shared);
        KeyboardShortcutManager.ShortcutsChangedLiveData.Observe(owner, _ => ViewModel.Refresh());
    }

    private void AddShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            EditKeyboardShortcutDialog dialog = new(null);
            await dialog.ShowAsync(ViewModel.Shared.WindowId);
            KeyboardShortcutModel? result = dialog.ViewModel.Result;
            if (result is not null)
            {
                ViewModel.Add(result);
            }
        });
    }

    private void ResetShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reset();
    }

    private void EditShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not KeyboardShortcutItemViewModel item)
        {
            return;
        }

        CoroutineUtils.Run(async () =>
        {
            EditKeyboardShortcutDialog dialog = new(item.Model);
            await dialog.ShowAsync(ViewModel.Shared.WindowId);
            KeyboardShortcutModel? result = dialog.ViewModel.Result;
            if (result is not null)
            {
                ViewModel.Update(item.Model, result);
            }
        });
    }

    private void RemoveShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is KeyboardShortcutItemViewModel item)
        {
            ViewModel.Remove(item.Model);
        }
    }
}
