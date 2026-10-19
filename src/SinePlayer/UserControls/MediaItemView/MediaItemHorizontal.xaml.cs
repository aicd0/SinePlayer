// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Core.Common.Utils;
using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.MediaItemView;

internal sealed partial class MediaItemHorizontal : BaseUserControl, IMediaItemView
{
    private bool _isPrimaryPointerPressed;

    public MediaItemHorizontal()
    {
        InitializeComponent();
    }

    public MediaItemViewModel? Item { get; private set; }

    public void SetMediaModel(MediaItemViewModel? item)
    {
        if (item == Item)
        {
            return;
        }

        Item = item;
        Bindings.Update();
    }

    private void BaseUserControl_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Selection mode doesn't work without this.
        e.Handled = true;
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "PointerOver", true);
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Normal", true);
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPrimaryPointerPressed = e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed;
    }

    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        bool isPrimaryPointerPressed = _isPrimaryPointerPressed;
        _isPrimaryPointerPressed = false;

        if (!isPrimaryPointerPressed)
        {
            return;
        }

        MediaItemViewModel? item = Item;
        if (item is null)
        {
            return;
        }

        item.OnClick?.Invoke(item);
    }

    private void RootGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        MediaItemViewModel? viewModel = Item;
        if (viewModel is null)
        {
            return;
        }

        args.Handled = true;

        CoroutineUtils.Run(async () =>
        {
            FlyoutBase? flyout = await viewModel.CreateContextFlyout();
            if (flyout is null)
            {
                return;
            }

            if (args.TryGetPosition(fe, out Windows.Foundation.Point point))
            {
                flyout.ShowAt(fe, new FlyoutShowOptions { Position = point });
            }
            else
            {
                flyout.ShowAt(fe);
            }
        });
    }
}
