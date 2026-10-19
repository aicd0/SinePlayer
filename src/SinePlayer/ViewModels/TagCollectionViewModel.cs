// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Expression;
using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.Algorithm;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Helpers.MenuFlyoutHelpers;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.Views.Dialogs.EditTag;

namespace SinePlayer.ViewModels;

internal partial class TagCollectionViewModel(string name) : BaseViewModel, INotifyPropertyChanged
{
    public static readonly TagCollectionViewModel Default = new(string.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name = name;
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public ObservableCollection<TagViewModel> Tags { get; } = [];

    public override void NotifyImmediately()
    {
        for (int i = 0; i < Tags.Count; i++)
        {
            Tags[i] = Tags[i];
        }
    }

    //
    // Construction
    //

    public static void Update(ObservableCollection<TagCollectionViewModel> target, MediaModel? media, ActionHandler actionHandler)
    {
        List<TagCollectionViewModel> newCollection = Create(media, actionHandler);
        DiffUtils.UpdateCollection(target, newCollection, (x, y) => x.Name == y.Name, (x, y) =>
        {
            DiffUtils.UpdateCollection(x.Tags, y.Tags, (a, b) => a.Tag == b.Tag, (a, b) =>
            {
                a.OnRequestContextFlyoutAsync = b.OnRequestContextFlyoutAsync;
                a.OnClicked = b.OnClicked;
            });
        });
    }

    private static List<TagCollectionViewModel> Create(MediaModel? media, ActionHandler actionHandler)
    {
        List<TagCollectionViewModel> newCollection = [];
        if (media is null)
        {
            return newCollection;
        }

        foreach (KeyValuePair<string, MediaTagCategory> tagItem in media.Tags)
        {
            string tagCategory = tagItem.Key;
            List<TagViewModel> tagModels = [];
            foreach (string tag in tagItem.Value.Tags)
            {
                string tagName = tag;
                TagViewModel tagModel = new()
                {
                    Tag = tagName,
                    OnClicked = () => OpenTagSearch(actionHandler, tagCategory, tagName),
                    OnRequestContextFlyoutAsync = () => CreateTagContextMenuItems(media, actionHandler, tagCategory, tagName),
                };
                tagModels.Add(tagModel);
            }

            tagModels.Sort((a, b) => string.Compare(a.Tag, b.Tag, ignoreCase: true));
            var tagCollectionModel = new TagCollectionViewModel(tagItem.Key);
            foreach (TagViewModel tagModel in tagModels)
            {
                tagCollectionModel.Tags.Add(tagModel);
            }

            newCollection.Add(tagCollectionModel);
        }

        newCollection.Sort((a, b) => string.Compare(a.Name, b.Name, ignoreCase: true));
        return newCollection;
    }

    private static void OpenTagSearch(ActionHandler actionHandler, string tagCategory, string tag)
    {
        string expression = $"%{MediaSQLProviderUtils.VAR_TAG}.\"{ExpressionUtils.EscapeString(tagCategory)}\"=\"{ExpressionUtils.EscapeString(tag)}\"";
        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_SEARCH)
            .WithParam(RouterConstants.ARG_KEYWORD, $"exp:\"{ExpressionUtils.EscapeString(expression)}\"");
        ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
            .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
            .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
            .Build();
        actionHandler.HandleNoResult(actionModel);
    }

    private static async Task<List<BaseMenuFlyoutItemModel>> CreateTagContextMenuItems(MediaModel media, ActionHandler actionHandler, string tagCategory, string tag)
    {
        List<BaseMenuFlyoutItemModel> items = [];

        items.Add(new SubItemMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Links,
            Icon = new FontIconSource() { Glyph = "\uE71B" },
            Items = await MenuFlyoutItemsCreator.CreateTagLinkMenuItems(tagCategory, tag, actionHandler),
        });

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Edit,
            Icon = new FontIconSource() { Glyph = "\uE70F" },
            Click = () =>
            {
                if (!actionHandler.TryGetComponent(out IMainWindowComponent? mainWindowCom))
                {
                    return;
                }

                var dialog = new EditTagDialog(tagCategory, tag);
                CoroutineUtils.Run(() => dialog.ShowAsync(mainWindowCom.WindowId));
            },
        });

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Delete,
            Icon = new FontIconSource() { Glyph = "\uE74D" },
            Click = () =>
            {
                CoroutineUtils.Run(async () =>
                {
                    Dictionary<string, HashSet<string>> tags = media.TagsCopy;
                    if (tags.TryGetValue(tagCategory, out HashSet<string>? tagSet))
                    {
                        if (tagSet.Remove(tag))
                        {
                            await media.SetTags(tags);
                        }
                    }
                });
            },
        });

        return items;
    }
};
