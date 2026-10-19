// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Misc;
using SinePlayer.Core.Common.AppEnvironment;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.ViewModels;

namespace SinePlayer.Views.Pages.Sidebar.History;

internal sealed partial class HistoryPage : BasePage
{
    public ObservableCollection<HistoryGroupViewModel> DataSource { get; set; } = [];

    public HistoryPage()
    {
        InitializeComponent();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        CoroutineUtils.Run(Update);
        ObserveData();
    }

    protected override void OnResume()
    {
        base.OnResume();
    }

    private void ObserveData()
    {
        GlobalEvent.Instance.HistoryUpdated.Observe(this, _ =>
        {
            CoroutineUtils.Run(Update);
        });
    }

    private async Task Update()
    {
        var source = new List<HistoryGroupViewModel>();
        HistoryGroupViewModel? currentGroup = null;
        List<MediaHistoryItemModel> historyItems = await MediaHistoryItemModel.GetAllAsync();
        historyItems.Sort((x, y) => y.DateTime.CompareTo(x.DateTime));

        List<MediaModel> mediaItems = await MediaModel.BatchFromId(historyItems.Select(x => x.MediaId));
        Dictionary<long, MediaModel> mediaMap = new();
        foreach (MediaModel media in mediaItems)
        {
            mediaMap[media.Id] = media;
        }

        foreach (MediaHistoryItemModel item in historyItems)
        {
            DateTimeOffset localTime = item.DateTime.ToLocalTime();
            string key = localTime.ToString("D", EnvironmentProvider.Instance.GetCurrentAppLanguageInfo());
            if (currentGroup != null && !currentGroup.Key.Equals(key))
            {
                source.Add(currentGroup);
                currentGroup = null;
            }

            currentGroup ??= new HistoryGroupViewModel(key);
            mediaMap.TryGetValue(item.MediaId, out MediaModel? media);
            var itemOut = new HistoryItemViewModel
            {
                Id = item.MediaId,
                Media = media,
                Time = localTime.ToString("t", EnvironmentProvider.Instance.GetCurrentAppLanguageInfo()),
                Title = item.Title
            };

            currentGroup.Add(itemOut);
        }

        if (currentGroup != null)
        {
            source.Add(currentGroup);
        }

        DataSource.Clear();
        foreach (HistoryGroupViewModel item in source)
        {
            DataSource.Add(item);
        }

        MainListView.SelectedIndex = -1;
        TbNoHistory.Visibility = source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task OpenItem(HistoryItemViewModel item, bool newTab)
    {
        MediaModel? media = item.Media ?? await MediaModel.FromId(item.Id);
        if (media is null)
        {
            DeleteItem(item);
            return;
        }

        IEnumerable<MediaModel> playQueueMedia = DataSource.SelectMany(x => x).Select(x => x.Media).OfType<MediaModel>();
        PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(playQueueMedia);
        if (newTab)
        {
            Route route = OpenMediaHelper.GetMediaRoute(media, playQueue: playQueue);
            GetMainPageAbility().OpenInNewTab(route);
        }
        else
        {
            Route route = OpenMediaHelper.GetMediaRoute(media, playQueue: playQueue);
            OpenMediaHelper.OpenMedia(PageActionHandler, route);
        }

        GetMainPageAbility().SetSidePaneOpenState(false, force: false);
    }

    private IMainPageAbility GetMainPageAbility()
    {
        return GetAbility<IMainPageAbility>()!;
    }

    private void DeleteItem(HistoryItemViewModel item)
    {
        CoroutineUtils.Run(() => MediaHistoryItemModel.RemoveAsync(item.Id, suppressEvent: true));
        var source = (ObservableCollection<HistoryGroupViewModel>)HistorySource.Source;

        for (int i = 0; i < source.Count; ++i)
        {
            HistoryGroupViewModel group = source[i];

            for (int j = 0; j < group.Count; ++j)
            {
                HistoryItemViewModel item2 = group[j];

                if (item2.Id == item.Id)
                {
                    group.RemoveAt(j);
                    --j;
                }
            }

            if (group.Count == 0)
            {
                source.RemoveAt(i);
                --i;
            }
        }

        TbNoHistory.Visibility = source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // events
    private void OnOpenInNewTabClicked(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            var item = (HistoryItemViewModel)((MenuFlyoutItem)sender).DataContext;
            await OpenItem(item, true);
        });
    }

    private void OnDeleteItemClicked(object sender, RoutedEventArgs e)
    {
        var item = (HistoryItemViewModel)((MenuFlyoutItem)sender).DataContext;
        DeleteItem(item);
    }

    private void MainListViewItemClick(object sender, ItemClickEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            var item = (HistoryItemViewModel)e.ClickedItem;
            await OpenItem(item, false);
        });
    }
}
