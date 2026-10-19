// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Localization;
using SinePlayer.Data.Models.Misc;

namespace SinePlayer.UserControls.Misc;

internal sealed partial class ViewTypeSelector : BaseUserControl
{
    public delegate void ViewTypeChangedEventHandler(MediaFilterModel.ViewTypeEnum viewType);
    public event ViewTypeChangedEventHandler? ViewTypeChanged;

    private static readonly List<MediaFilterModel.ViewTypeEnum> _viewTypes = [
        MediaFilterModel.ViewTypeEnum.Large,
        MediaFilterModel.ViewTypeEnum.Medium,
    ];

    public MediaFilterModel.ViewTypeEnum SelectedViewType
    {
        get => (MediaFilterModel.ViewTypeEnum)GetValue(SelectedViewTypeProperty);
        set => SetValue(SelectedViewTypeProperty, value);
    }

    public static readonly DependencyProperty SelectedViewTypeProperty = DependencyProperty.Register(
        nameof(SelectedViewType),
        typeof(MediaFilterModel.ViewTypeEnum),
        typeof(ViewTypeSelector),
        new PropertyMetadata(MediaFilterModel.ViewTypeEnum.Medium));

    public ViewTypeSelector()
    {
        InitializeComponent();

        var flyout = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };
        flyout.Opening += (sender, args) => RebuildItems(flyout);
        MainButton.Flyout = flyout;
    }

    private void RebuildItems(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        foreach (MediaFilterModel.ViewTypeEnum viewType in _viewTypes)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = ViewTypeToDisplayName(viewType),
                IsChecked = viewType == SelectedViewType,
            };
            MediaFilterModel.ViewTypeEnum selected = viewType;
            item.Click += (sender, args) => ViewTypeChanged?.Invoke(selected);
            flyout.Items.Add(item);
        }
    }

    private static string ViewTypeToDisplayName(MediaFilterModel.ViewTypeEnum viewType)
    {
        return viewType switch
        {
            MediaFilterModel.ViewTypeEnum.Large => StringResourceProvider.Instance.ViewTypeLarge,
            MediaFilterModel.ViewTypeEnum.Medium => StringResourceProvider.Instance.ViewTypeMedium,
            _ => "Unknown"
        };
    }
}
