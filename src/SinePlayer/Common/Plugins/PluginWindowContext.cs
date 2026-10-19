// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Utils;
using SinePlayer.SDK.Models;
using SinePlayer.SDK.Plugins.Media;
using SinePlayer.SDK.Plugins.UI;

namespace SinePlayer.Common.Plugins;

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

    public void SetForegroundMedia(IMediaModel? media, Func<Action?, bool> executor)
    {
        if (media == _foregroundMedia)
        {
            return;
        }

        _foregroundMedia = media;
        executor(() => _foregroundMediaChanged?.Invoke(media));
    }

    //
    // IWindowContext Implementation
    //

    private event ForegroundMediaChangedEventHandler? _foregroundMediaChanged;

    private IMediaModel? _foregroundMedia;

    event ForegroundMediaChangedEventHandler? IWindowContext.ForegroundMediaChanged
    {
        add { _foregroundMediaChanged += value; }
        remove { _foregroundMediaChanged -= value; }
    }

    public IMediaModel? ForegroundMedia => _foregroundMedia;

    Task<DialogResult> IWindowContext.EnqueueDialog(DialogOptions options)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, options);
    }

    Task<DialogResult> IWindowContext.EnqueueDialog(ContentDialog dialog)
    {
        return DialogUtils.EnqueueDialogAsync(_windowId, dialog);
    }
}
