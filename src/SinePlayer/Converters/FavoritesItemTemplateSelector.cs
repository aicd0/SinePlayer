// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

#nullable disable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.ViewModels;

namespace SinePlayer.Converters;

internal partial class FavoritesItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate NormalTemplate { get; set; }
    public DataTemplate RenamingTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var model = item as FavoriteItemViewModel;
        return model.IsRenaming ? RenamingTemplate : NormalTemplate;
    }
};