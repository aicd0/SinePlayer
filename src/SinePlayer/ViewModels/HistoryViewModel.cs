// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.ObjectModel;

using SinePlayer.Common.BaseUI;
using SinePlayer.Data.Models.Media;

namespace SinePlayer.ViewModels;

internal class HistoryItemViewModel : BaseViewModel
{
    public MediaModel? Media { get; set; }
    public long Id { get; set; }
    public string Time { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public string ItemGlyph => Media is null ? "\uE9CE" : (Media.IsCollection ? "\uF5ED" : "\uE8B9");
}

internal class HistoryGroupViewModel : ObservableCollection<HistoryItemViewModel>
{
    public HistoryGroupViewModel(string key) : base()
    {
        Key = key;
    }

    public string Key { get; set; }
}
