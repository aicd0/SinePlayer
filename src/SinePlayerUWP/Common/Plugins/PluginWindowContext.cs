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

    public void SetReadingComic(IComicModel? media, Func<Action?, bool> executor)
    {
        if (media == _readingMedia)
        {
            return;
        }

        _readingMedia = media;
        executor(() => _readingMediaChanged?.Invoke(media));
    }

    //
    // IWindowContext Implementation
    //

    private event ReadingComicChangedEventHandler? _readingMediaChanged;

    private IComicModel? _readingMedia;

    event ReadingComicChangedEventHandler? IWindowContext.ReadingComicChanged
    {
        add { _readingMediaChanged += value; }
        remove { _readingMediaChanged -= value; }
    }

    public IComicModel? ReadingComic => _readingMedia;

    Task<DialogResult> IWindowContext.EnqueueDialog(DialogOptions options)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, options);
    }

    Task<DialogResult> IWindowContext.EnqueueDialog(ContentDialog dialog)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, dialog);
    }
}
