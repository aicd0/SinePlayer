// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Data.Models.Media;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Misc;

namespace SinePlayer.Views.Pages.Sidebar.PlayQueue;

internal partial class PlayQueueItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaModel? Media { get; set; }

    public string Glyph => MediaGlyphHelper.GetMediaGlyph(Media);

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    private string _progress = string.Empty;
    public string Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        }
    }

    public Func<PlayQueueItemViewModel, Task<List<BaseMenuFlyoutItemModel>>>? RequestContextMenuItemsAsync { get; set; }

    public async Task<FlyoutBase?> CreateContextFlyout()
    {
        if (RequestContextMenuItemsAsync is null)
        {
            return null;
        }

        List<BaseMenuFlyoutItemModel> menuFlyoutItems = await RequestContextMenuItemsAsync(this);
        if (menuFlyoutItems.Count == 0)
        {
            return null;
        }

        var flyout = new MenuFlyout();
        foreach (BaseMenuFlyoutItemModel item in menuFlyoutItems)
        {
            flyout.Items.Add(item.CreateMenuFlyoutItem());
        }

        return flyout;
    }
}
