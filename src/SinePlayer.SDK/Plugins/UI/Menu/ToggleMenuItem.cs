// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.SDK.Plugins.UI.Menu;

public sealed class ToggleMenuItem : IMenuItem
{
    public required string Text { get; set; }
    public bool IsChecked { get; set; } = false;
    public Action? Click { get; set; }
}
