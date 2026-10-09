// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

#nullable disable

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayerUWP.ViewModels;

namespace SinePlayerUWP.Converters;

internal partial class FolderItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate NormalTemplate { get; set; }
    public DataTemplate AddNewTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var folder_item = (FolderItemViewModel)item;
        return folder_item.IsAddNew ? AddNewTemplate : NormalTemplate;
    }
};