// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.UserControls.MediaItemView;
using SinePlayer.ViewModels;

namespace SinePlayer.UserControls.MediaSelection;

internal sealed partial class MediaSelectionView : BaseUserControl
{
    private const string TAG = nameof(MediaSelectionView);

    public delegate void GroupCollapseRequestedEventHandler(MediaGroupViewModel group);
    public event GroupCollapseRequestedEventHandler? GroupCollapseRequested;

    public delegate void ScrollOffsetChangedEventHandler(double verticalOffset);
    public event ScrollOffsetChangedEventHandler? ScrollOffsetChanged;

    public MediaFilterModel.ViewTypeEnum ItemViewType
    {
        get => (MediaFilterModel.ViewTypeEnum)GetValue(ItemViewTypeProperty);
        set => SetValue(ItemViewTypeProperty, value);
    }

    public static readonly DependencyProperty ItemViewTypeProperty = DependencyProperty.Register(
        nameof(ItemViewType),
        typeof(MediaFilterModel.ViewTypeEnum),
        typeof(MediaSelectionView),
        new PropertyMetadata(MediaFilterModel.ViewTypeEnum.Medium, OnItemViewTypeChanged));

    private static void OnItemViewTypeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((MediaSelectionView)sender).ApplyItemViewType();
    }

    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public static readonly DependencyProperty ContentPaddingProperty = DependencyProperty.Register(
        nameof(ContentPadding),
        typeof(Thickness),
        typeof(MediaSelectionView),
        new PropertyMetadata(new Thickness(0), OnContentPaddingChanged));

    private static void OnContentPaddingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((MediaSelectionView)sender).ApplyContentPadding();
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent),
        typeof(object),
        typeof(MediaSelectionView),
        new PropertyMetadata(null));

    public MediaSelectionViewModel? ViewModel { get; set; }

    private readonly ObservableCollection<MediaItemViewModel> _items = [];
    private readonly ObservableCollection<MediaGroupViewModel> _groups = [];
    private readonly ICollectionView _groupedView;
    private ScrollViewer? _itemsScrollViewer;
    private bool? _usingGroupedSource = null;
    private ActionHandler _actionHandler = ActionHandler.Dummy;

    public MediaSelectionView()
    {
        InitializeComponent();

        GroupedItemSource.Source = _groups;
        _groupedView = GroupedItemSource.View;

        ApplyContentPadding();
        ApplyItemViewType();
    }

    public void Initialize(MediaSelectionViewModel viewModel, ActionHandler actionHandler)
    {
        ViewModel = viewModel;
        _actionHandler = actionHandler;
        Bindings.Update();
    }

    public void SetItems(IEnumerable<MediaItemViewModel> items)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        List<MediaItemViewModel> newItems = [.. items];
        DiffUtils.UpdateCollection(_items, newItems, (x, y) => x.Media.Id == y.Media.Id, (x, y) => x.Update(y));
        ApplyItemsSource(false);
        viewModel.UpdateCommandBarButtonStates();
    }

    public void SetGroupedItems(IEnumerable<MediaGroupViewModel> groups)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        List<MediaGroupViewModel> newGroups = [.. groups];

        // Disable ME as it's causing a native crash in Microsoft.ui.xaml.dll. This is not guaranteed a fix
        // but so far it works fine. How to reproduce: Under group view (with 20+ groups), scroll to bottom
        // (or close to bottom) of the list. Then switch between different filter presets which share the
        // same group names, the crash should occur.
        DiffUtils.UpdateCollection(_groups, newGroups, (x, y) => x.GroupName == y.GroupName, (x, y) =>
        {
            x.Collapsed = y.Collapsed;
            x.Description = y.Description;
            x.UpdateItems(y.Items, (a, b) => a.Media.Id == b.Media.Id, (a, b) => a.Update(b));
        }, disableME: true);

        ApplyItemsSource(true);
        viewModel.UpdateCommandBarButtonStates();
    }

    private void ApplyItemsSource(bool grouped)
    {
        if (_usingGroupedSource == grouped)
        {
            return;
        }

        _usingGroupedSource = grouped;
        ItemsGrid.ItemsSource = grouped ? _groupedView : _items;
    }

    private void ApplyItemViewType()
    {
        switch (ItemViewType)
        {
            case MediaFilterModel.ViewTypeEnum.Large:
                ItemsGrid.ItemTemplate = LargeItemTemplate;
                ItemsGrid.ItemContainerStyle = (Style)Resources["VerticalMediaItemContainerStyle"];
                ItemsGrid.DesiredWidth = (double)Application.Current.Resources["MediaItemVerticalDesiredWidth"];
                ItemsGrid.ItemHeight = (double)Application.Current.Resources["MediaItemVerticalDesiredHeight"];
                break;
            case MediaFilterModel.ViewTypeEnum.Medium:
                ItemsGrid.ItemTemplate = MediumItemTemplate;
                ItemsGrid.ItemContainerStyle = (Style)Resources["SearchResultItemContainerExpandedStyle"];
                ItemsGrid.DesiredWidth = (double)Application.Current.Resources["MediaItemHorizontalDesiredWidth"];
                ItemsGrid.ItemHeight = (double)Application.Current.Resources["MediaItemHorizontalDesiredHeight"];
                break;
            default:
                Logger.AssertNotReachHere("8A1F72C69B4D5307");
                break;
        }
    }

    private void ApplyContentPadding()
    {
        ItemsGrid.Padding = ContentPadding;
    }

    //
    // Command Bar
    //

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not AppBarToggleButton button)
        {
            return;
        }

        if (button.IsChecked == true)
        {
            ItemsGrid.SelectAll();
        }
        else
        {
            ItemsGrid.DeselectRange(new ItemIndexRange(0, (uint)ItemsGrid.Items.Count));
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        IReadOnlyList<MediaModel> mediaItems = viewModel.GetSelectedMedia();
        FavoriteModel.Instance.BatchAdd([.. mediaItems.Select(x => new FavoriteModel.FavoriteItem
        {
            Id = x.Id,
            Title = x.Title,
        })]);
    }

    private void UnfavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        IReadOnlyList<MediaModel> mediaItems = viewModel.GetSelectedMedia();
        FavoriteModel.Instance.BatchRemoveWithId([.. mediaItems.Select(x => x.Id)]);
    }

    private void CompletionStatusButton_Click(object sender, RoutedEventArgs e)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        IReadOnlyList<MediaModel> mediaItems = viewModel.GetSelectedMedia();
        List<BaseMenuFlyoutItemModel> menuItems = MenuFlyoutItemsCreator.CreateCompletionStatusMenuItems(mediaItems);

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in menuItems)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMediaHidden(true);
    }

    private void UnhideButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedMediaHidden(false);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        IReadOnlyList<MediaModel> mediaItems = viewModel.GetSelectedMedia();
        string idList = string.Join(',', mediaItems.Select(x => x.Id.ToString()));
        ActionModel actionModel = ActionModel.Builder.Create(RemoveMediaProvider.NAME)
            .AddParameter(RemoveMediaProvider.PARAM_MEDIA_ID, idList)
            .Build();
        _actionHandler.HandleNoResult(actionModel);
    }

    private void SetSelectedMediaHidden(bool hidden)
    {
        MediaSelectionViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        IReadOnlyList<MediaModel> mediaItems = viewModel.GetSelectedMedia();
        CoroutineUtils.Run(() => BusyStateManager.WithBusyState(() =>
        {
            return Task.WhenAll(mediaItems.Select(x => x.SetHidden(hidden)));
        }));
    }

    //
    // Item grid
    //

    private void ItemsGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not IMediaItemView viewHolder || args.Item is not MediaItemViewModel item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            viewHolder.SetMediaModel(null);
        }
        else
        {
            viewHolder.SetMediaModel(item);
        }
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel?.SetSelection(ItemsGrid.SelectedItems.OfType<MediaItemViewModel>());
    }

    private void ItemsGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel?.SetSelectMode(false);
    }

    private void ItemsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsLoaded)
        {
            return;
        }

        ScrollViewer? scrollViewer = ItemsGrid.ChildrenBreadthFirst().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer != null)
        {
            _itemsScrollViewer = scrollViewer;
            scrollViewer.ViewChanged += ItemsScrollViewer_ViewChanged;
        }
        else
        {
            Logger.AssertNotReachHere("2E6B41D8795C0FA3");
        }
    }

    private void ItemsGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.IsLoaded)
        {
            return;
        }

        ScrollViewer? scrollViewer = _itemsScrollViewer;
        if (scrollViewer != null)
        {
            scrollViewer.ViewChanged -= ItemsScrollViewer_ViewChanged;
        }
        _itemsScrollViewer = null;
    }

    private void ItemsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            ScrollOffsetChanged?.Invoke(scrollViewer.VerticalOffset);
        }
    }

    private void GroupCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MediaGroupViewModel group)
        {
            GroupCollapseRequested?.Invoke(group);
        }
    }
}
