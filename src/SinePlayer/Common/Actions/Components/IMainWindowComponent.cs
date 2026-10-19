// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Plugins;

namespace SinePlayer.Common.Actions.Components;

internal interface IMainWindowComponent : IActionComponent
{
    int WindowId { get; }

    PluginWindowContext PluginWindowContext { get; }
}
