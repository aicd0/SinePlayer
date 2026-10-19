// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml.Controls;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal class SeparatorMenuFlyoutItemModel : BaseMenuFlyoutItemModel
{
    protected override MenuFlyoutItemBase CreateMenuFlyoutItemInternal()
    {
        return new MenuFlyoutSeparator();
    }
}
