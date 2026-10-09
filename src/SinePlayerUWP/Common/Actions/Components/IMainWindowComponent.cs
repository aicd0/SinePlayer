// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayerUWP.Common.Plugins;

namespace SinePlayerUWP.Common.Actions.Components;

internal interface IMainWindowComponent : IActionComponent
{
    int WindowId { get; }

    PluginWindowContext PluginWindowContext { get; }
}
