// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Constants;
using SinePlayer.Common.Misc;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Views.Pages.Sidebar.PlayQueue;

internal sealed partial class PlayQueuePage : BasePage
{
    private const string TAG = nameof(PlayQueuePage);

    private readonly PlayQueuePageViewModel ViewModel = new();

    public PlayQueuePage()
    {
        InitializeComponent();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);
        ViewModel.Initialize(PageActionHandler);
        ObserveData();
    }

    protected override void OnStop()
    {
        base.OnStop();
        ViewModel.Destory();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.MediaUpdated.Observe(this, _ =>
        {
            ViewModel.UpdateMedia();
        });

        GetWindowEventBus().With<PlaybackModel>(EventId.PlaybackChanged).ObserveSticky(this, ViewModel.SetPlayback);

        ViewModel.ScrollToItemLiveData.ObserveSticky(this, MainListView.ScrollIntoView);
    }

    //
    // Events
    //

    private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedIndex(((ListView)sender).SelectedIndex);
    }

    private void ListView_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement fe)
        {
            return;
        }

        if (fe.DataContext is not PlayQueueItemViewModel viewModel)
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
