// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.ObjectModel;
using System.ComponentModel;

using SinePlayer.Common.BaseUI;
using SinePlayer.Data.Models.Media;
using SinePlayer.Helpers.Misc;

namespace SinePlayer.ViewModels;

internal enum FavoriteNodeType
{
    Item,
    Filter
};

internal partial class FavoriteItemViewModel : BaseViewModel, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public FavoriteItemViewModel(string name, FavoriteNodeType type)
    {
        Name = name;
        EditingName = name;
        Type = type;
        Children = [];
        IsRenaming = false;
        _expanded = false;
    }

    public MediaModel? Media { get; set; }
    public string Name { get; set; }
    public string EditingName { get; set; }
    public long Id { get; set; }
    public bool IsRenaming { get; set; }

    private bool _expanded;
    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Expanded"));
        }
    }

    public ObservableCollection<FavoriteItemViewModel> Children { get; set; }
    public FavoriteItemViewModel? Parent { get; set; }
    public FavoriteNodeType Type { get; set; }

    public bool AllowDrop
    {
        get => Type == FavoriteNodeType.Filter;
        set { Type = value ? FavoriteNodeType.Filter : FavoriteNodeType.Item; }
    }

    public bool IsItem
    {
        get => Type == FavoriteNodeType.Item;
        set { Type = value ? FavoriteNodeType.Item : FavoriteNodeType.Filter; }
    }

    public string ItemGlyph => Media is null ?
        "\uE9CE" : (Media.IsCollection ? "\uF5ED" : MediaGlyphHelper.GetMediaGlyph(Media));

    public void FixParent()
    {
        foreach (FavoriteItemViewModel child in Children)
        {
            child.Parent = this;
            child.FixParent();
        }
    }
};
