// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml.Controls;

namespace SinePlayer.SDK.Plugins.UI.Menu;

public sealed class SimpleMenuItem : IMenuItem
{
    public required string Text { get; set; }
    public IconSource? Icon { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Action? Click { get; set; }
}
