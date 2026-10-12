// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.Misc;

internal sealed partial class MediaTagsView : UserControl
{
    public MediaTagsView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<TagCollectionViewModel>? Tags
    {
        get => (IReadOnlyList<TagCollectionViewModel>?)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags),
        typeof(IReadOnlyList<TagCollectionViewModel>),
        typeof(MediaTagsView),
        new PropertyMetadata(null));
}
