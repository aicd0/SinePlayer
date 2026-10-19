// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml.Controls;

namespace SinePlayer.SDK.Plugins.UI;

public interface ISidebarPageProvider
{
    string Name { get; }

    string Host { get; }

    IconSource Icon { get; }
}
