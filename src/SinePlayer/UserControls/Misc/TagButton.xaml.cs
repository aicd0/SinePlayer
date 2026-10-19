// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Core.Common.Utils;
using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.Misc;

internal sealed partial class TagButton : UserControl
{
    public TagViewModel? ViewModel => DataContext as TagViewModel;

    public TagButton()
    {
        InitializeComponent();
        DataContextChanged += (s, e) => Bindings.Update();
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OnClicked?.Invoke();
    }

    private void Button_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }

        TagViewModel? viewModel = ViewModel;
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
