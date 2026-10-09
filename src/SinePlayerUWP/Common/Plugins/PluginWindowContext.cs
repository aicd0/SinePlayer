// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.Common.Actions;
using SinePlayerUWP.Common.Actions.Components;
using SinePlayerUWP.Common.BaseUI;
using SinePlayerUWP.Common.BaseUI.PageAbilities;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.SDK.Models;
using SinePlayerUWP.SDK.Plugins.Comic;
using SinePlayerUWP.SDK.Plugins.UI;

namespace SinePlayerUWP.Common.Plugins;

internal sealed class PluginWindowContext(int windowId) : IWindowContext
{
    //
    // Factory
    //

    public static PluginWindowContext From(PageCommunicator communicator)
    {
        return communicator.GetAbility<IMainWindowAbility>()!.PluginWindowContext;
    }

    public static PluginWindowContext From(ActionHandler actionHandler)
    {
        if (!actionHandler.TryGetComponent(out IMainWindowComponent? component))
        {
            throw new ArgumentException("IMainWindowComponent not found.");
        }

        return component.PluginWindowContext;
    }

    //
    // Variables
    //

    private readonly int _windowId = windowId;

    //
    // Public API
    //

    public void SetReadingComic(IComicModel? comic, Func<Action?, bool> executor)
    {
        if (comic == _readingComic)
        {
            return;
        }

        _readingComic = comic;
        executor(() => _readingComicChanged?.Invoke(comic));
    }

    //
    // IWindowContext Implementation
    //

    private event ReadingComicChangedEventHandler? _readingComicChanged;

    private IComicModel? _readingComic;

    event ReadingComicChangedEventHandler? IWindowContext.ReadingComicChanged
    {
        add { _readingComicChanged += value; }
        remove { _readingComicChanged -= value; }
    }

    public IComicModel? ReadingComic => _readingComic;

    Task<DialogResult> IWindowContext.EnqueueDialog(DialogOptions options)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, options);
    }

    Task<DialogResult> IWindowContext.EnqueueDialog(ContentDialog dialog)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, dialog);
    }
}
