// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using SinePlayer.Common.Actions;
using SinePlayer.Common.Actions.Components;
using SinePlayer.Common.Actions.Providers;
using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Expression;
using SinePlayer.Common.Localization;
using SinePlayer.Common.Misc;
using SinePlayer.Common.Plugins;
using SinePlayer.Common.Storage;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;
using SinePlayer.Data.Models.TagInfo;
using SinePlayer.Helpers.Misc;
using SinePlayer.Helpers.Navigation;
using SinePlayer.Helpers.Search;
using SinePlayer.Views.Dialogs.EditMediaInfo;

namespace SinePlayer.Helpers.MenuFlyoutHelpers;

internal static class MenuFlyoutItemsCreator
{
    public const string CUSTOM_ACTION_SOURCE_MEDIA_ITEM_MENU = "MediaItemMenu";
    public const string CUSTOM_ACTION_NAME_SELECT = "Select";

    public static async Task<List<BaseMenuFlyoutItemModel>> CreateMediaMenuItems(
        ActionHandler actionHandler,
        MediaModel primaryItem,
        PlayQueueModel.Builder? playQueue = null,
        PlaybackModel.Builder? playback = null,
        IEnumerable<MediaModel>? selectedItems = null,
        bool canOpenWithDefault = false,
        bool canEdit = true,
        bool canSelect = false)
    {
        if (selectedItems is null)
        {
            selectedItems = [primaryItem];
        }
        else
        {
            if (selectedItems.Any(i => i.Id == primaryItem.Id))
            {
                playQueue = new PlayQueueModel.Builder().AddMedia(selectedItems);
                canOpenWithDefault = true;
            }
            else
            {
                selectedItems = [primaryItem];
            }
        }

        List<MediaModel> inLibraryItems = [.. selectedItems.Where(x => !x.IsExternal)];
        List<MediaModel> inLibraryMedia = [.. inLibraryItems.Where(x => !x.IsCollection)];

        Route primaryMediaRoute = OpenMediaHelper.GetMediaRoute(
            primaryItem,
            playQueue: playQueue,
            playback: playback);

        List<BaseMenuFlyoutItemModel> items = [];

        if (canOpenWithDefault)
        {
            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Open,
                Icon = new FontIconSource() { Glyph = MediaGlyphHelper.GetMediaGlyph(primaryItem) },
                Click = () =>
                {
                    OpenMediaHelper.OpenMedia(actionHandler, primaryMediaRoute);
                },
            });
        }

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.OpenInNewTab,
            Icon = new FontIconSource() { Glyph = "\uE8A5" },
            Click = () =>
            {
                ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                    .AddParameter(OpenTabProvider.PARAM_URL, primaryMediaRoute.Url)
                    .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
                    .Build();
                actionHandler.HandleNoResult(actionModel);
            },
        });

        items.Add(new SubItemMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.SendToWindow,
            Icon = new FontIconSource() { Glyph = "\uE78B" },
            Items = CreateSendToWindowMenuItems(actionHandler, primaryMediaRoute),
        });

        if (!primaryItem.IsCollection)
        {
            items.Add(new SeparatorMenuFlyoutItemModel());

            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.ShowInFileExplorer,
                Icon = new FontIconSource() { Glyph = "\uE838" },
                Click = () =>
                {
                    CoroutineUtils.Run(async () =>
                    {
                        ErrorResult err = await primaryItem.ShowInFileExplorer();
                        err.DisplayErrorMessage(actionHandler);
                    });
                },
            });
        }

        items.Add(new SeparatorMenuFlyoutItemModel());

        items.Add(new SubItemMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Links,
            Icon = new FontIconSource() { Glyph = "\uE71B" },
            Items = await CreateMediaLinkMenuItems(primaryItem, actionHandler),
        });

        items.Add(new SubItemMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Tags,
            Icon = new FontIconSource() { Glyph = "\uE8EC" },
            Items = CreateMediaTagMenuItems(primaryItem, actionHandler),
        });

        if (canEdit && inLibraryItems.Count > 0)
        {
            items.Add(new SeparatorMenuFlyoutItemModel());

            bool isFavorite = FavoriteModel.Instance.FromId(primaryItem.Id) != null;
            if (isFavorite)
            {
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = StringResourceProvider.Instance.RemoveFromFavorites,
                    Icon = new FontIconSource() { Glyph = "\uE8D9" },
                    Click = () =>
                    {
                        FavoriteModel.Instance.BatchRemoveWithId(inLibraryItems.ConvertAll(x => x.Id));
                    },
                });
            }
            else
            {
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = StringResourceProvider.Instance.AddToFavorites,
                    Icon = new FontIconSource() { Glyph = "\uE734" },
                    Click = () =>
                    {
                        FavoriteModel.Instance.BatchAdd(inLibraryItems.ConvertAll(x => new FavoriteModel.FavoriteItem
                        {
                            Id = x.Id,
                            Title = x.Title,
                        }));
                    },
                });
            }

            if (inLibraryMedia.Count > 0)
            {
                items.Add(new SubItemMenuFlyoutItemModel()
                {
                    Text = StringResourceProvider.Instance.AddToCollection,
                    Icon = new FontIconSource() { Glyph = "\uF5ED" },
                    Items = await CreateAddToCollectionMenuItems(actionHandler, inLibraryMedia),
                });
            }

            items.Add(new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.SetCompletionState,
                Icon = new FontIconSource() { Glyph = "\uE7C1" },
                Items = CreateCompletionStatusMenuItems(inLibraryItems),
            });

            if (primaryItem.IsHidden)
            {
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = StringResourceProvider.Instance.Unhide,
                    Icon = new FontIconSource() { Glyph = "\uE7B3" },
                    Click = () =>
                    {
                        CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                        {
                            await Task.WhenAll(inLibraryItems.Select(x => x.SetHidden(false)));
                        }));
                    },
                });
            }
            else
            {
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = StringResourceProvider.Instance.Hide,
                    Icon = new FontIconSource() { Glyph = "\uED1A" },
                    Click = () =>
                    {
                        CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                        {
                            await Task.WhenAll(inLibraryItems.Select(x => x.SetHidden(true)));
                        }));
                    },
                });
            }

            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Edit,
                Icon = new FontIconSource() { Glyph = "\uE70F" },
                Click = () =>
                {
                    string idList = string.Join(',', inLibraryItems.Select(x => x.Id.ToString()));
                    ActionModel actionModel = ActionModel.Builder.Create(EditMediaProvider.NAME)
                        .AddParameter(EditMediaProvider.PARAM_MEDIA_ID, idList)
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                },
            });

            items.Add(new SeparatorMenuFlyoutItemModel());

            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Remove,
                Icon = new FontIconSource()
                {
                    Glyph = "\uE74D",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 69, 0)),
                },
                Click = () =>
                {
                    string idList = string.Join(',', inLibraryItems.Select(x => x.Id.ToString()));
                    ActionModel actionModel = ActionModel.Builder.Create(RemoveMediaProvider.NAME)
                        .AddParameter(RemoveMediaProvider.PARAM_MEDIA_ID, idList)
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                },
            });
        }

        {
            var windowContext = PluginWindowContext.From(actionHandler);
            var pluginItems = PluginManager.Instance.GetActivePlugins()
                .SelectMany(ctx => ctx.GetMediaMenuItems(windowContext, primaryItem, selectedItems))
                .ToImmutableList();
            if (pluginItems.Count > 0)
            {
                items.Add(new SeparatorMenuFlyoutItemModel());
                items.AddRange(pluginItems);
            }
        }

        if (canSelect)
        {
            items.Add(new SeparatorMenuFlyoutItemModel());
            items.Add(CreateSelectMenuItem(actionHandler));
        }

        return items;
    }

    public static async Task<List<BaseMenuFlyoutItemModel>> CreateTagLinkMenuItems(string tagCategory, string tag, ActionHandler actionHandler)
    {
        Dictionary<string, TagLinkModel.LinkModel> linkMap = [];

        TagCategoryInfoModel? tagCategoryInfo = await TagCategoryInfoModel.Get(tagCategory);
        if (tagCategoryInfo is not null)
        {
            var linkModel = TagLinkModel.Parse(tagCategoryInfo.GetExt(TagCategoryInfoExt.LINKS));
            foreach (TagLinkModel.LinkModel item in linkModel.Links)
            {
                item.ReplaceTagVariables(tagCategory, tag);
                linkMap[item.Name] = item;
            }
        }

        TagInfoModel? tagInfo = await TagInfoModel.Get(tagCategory, tag);
        if (tagInfo is not null)
        {
            var linkModel = TagLinkModel.Parse(tagInfo.GetExt(TagInfoExt.LINKS));
            foreach (TagLinkModel.LinkModel item in linkModel.Links)
            {
                item.ReplaceTagVariables(tagCategory, tag);
                linkMap[item.Name] = item;
            }
        }

        List<TagLinkModel.LinkModel> links = [.. linkMap.Values];
        links.Sort((a, b) => a.Name.CompareTo(b.Name));
        return CreateLinkMenuItems(actionHandler, links);
    }

    public static BaseMenuFlyoutItemModel CreateSelectMenuItem(ActionHandler actionHandler)
    {
        return new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.Select,
            Icon = new FontIconSource() { Glyph = "\uE762" },
            Click = () =>
            {
                ActionModel actionModel = ActionModel.Builder.Create(CustomActionProvider.NAME)
                    .AddParameter(CustomActionProvider.PARAM_SOURCE, CUSTOM_ACTION_SOURCE_MEDIA_ITEM_MENU)
                    .AddParameter(CustomActionProvider.PARAM_NAME, CUSTOM_ACTION_NAME_SELECT)
                    .Build();
                actionHandler.HandleNoResult(actionModel);
            },
        };
    }

    public static async Task<List<BaseMenuFlyoutItemModel>> CreateMediaGroupMenuItems(
        ActionHandler actionHandler,
        IReadOnlyList<MediaModel> mediaItems,
        Action expandAllHandler,
        Action collapseAllHandler,
        IList<BaseMenuFlyoutItemModel>? customItems = null)
    {
        List<BaseMenuFlyoutItemModel> result = [];

        MediaModel? randomMedia = mediaItems.Count > 0 ? mediaItems[Random.Shared.Next(mediaItems.Count)] : null;
        if (randomMedia is not null)
        {
            PlayQueueModel.Builder playQueue = new PlayQueueModel.Builder().AddMedia(mediaItems);
            result.Add(new SubItemMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.Random,
                Icon = new FontIconSource() { Glyph = "\uE8B1" },
                Items = await CreateMediaMenuItems(
                    actionHandler,
                    randomMedia,
                    playQueue: playQueue,
                    canOpenWithDefault: true),
            });
        }

        result.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.ExpandAll,
            Icon = new FontIconSource() { Glyph = "\uECCD" },
            Click = expandAllHandler,
        });

        result.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.CollapseAll,
            Icon = new FontIconSource() { Glyph = "\uF165" },
            Click = collapseAllHandler,
        });

        if (customItems is not null && customItems.Count > 0)
        {
            result.Add(new SeparatorMenuFlyoutItemModel());
            foreach (BaseMenuFlyoutItemModel item in customItems)
            {
                result.Add(item);
            }
        }

        result.Add(new SeparatorMenuFlyoutItemModel());
        result.Add(CreateSelectMenuItem(actionHandler));
        return result;
    }

    public static List<BaseMenuFlyoutItemModel> CreateCompletionStatusMenuItems(IEnumerable<MediaModel> selectedMedia)
    {
        List<MediaModel> mediaItems = [.. selectedMedia];

        CompletionStatusEnum? oldStatus = null;
        foreach (MediaModel media in mediaItems)
        {
            if (!oldStatus.HasValue)
            {
                oldStatus = media.CompletionStatus;
                continue;
            }

            if (oldStatus.Value != media.CompletionStatus)
            {
                oldStatus = null;
                break;
            }
        }

        List<BaseMenuFlyoutItemModel> items = [];

        foreach (CompletionStatusEnum status in CompletionStatusService.AllStatus)
        {
            bool same = oldStatus == status;
            items.Add(new ToggleMenuFlyoutItemModel()
            {
                Text = CompletionStatusService.EnumToString(status),
                IsChecked = same,
                Click = same ? null : () =>
                {
                    CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                    {
                        await Task.WhenAll(selectedMedia.Select(x => x.SetCompletionStatus(status)));
                    }));
                },
            });
        }

        return items;
    }

    private static async Task<IEnumerable<BaseMenuFlyoutItemModel>> CreateAddToCollectionMenuItems(ActionHandler actionHandler, IReadOnlyList<MediaModel> selectedMedia)
    {
        int windowId = -1;
        if (actionHandler.TryGetComponent(out IMainWindowComponent? mainWindowCom))
        {
            windowId = mainWindowCom.WindowId;
        }

        List<MediaModel> mediaItems = [.. selectedMedia.Where(x => !x.IsExternal && !x.IsCollection)];

        List<BaseMenuFlyoutItemModel> items = [];

        IReadOnlyList<long> collectionIds = await CollectionModel.GetAllCollectionIds();
        if (collectionIds.Count > 0)
        {
            List<MediaModel> collections = await MediaModel.BatchFromId(collectionIds);
            IReadOnlySet<long> linkedCollectionIds = await CollectionModel.GetCollectionIdsContainingAll(mediaItems.Select(x => x.Id));
            foreach (MediaModel collection in collections)
            {
                bool isLinked = linkedCollectionIds.Contains(collection.Id);

                items.Add(new ToggleMenuFlyoutItemModel()
                {
                    Text = TrimText(collection.Title),
                    IsChecked = isLinked,
                    Click = () =>
                    {
                        CoroutineUtils.Run(() => BusyStateManager.WithBusyState(async () =>
                        {
                            if (isLinked)
                            {
                                await CollectionModel.RemoveMedia(collection, mediaItems.Select(x => x.Id));
                            }
                            else
                            {
                                await CollectionModel.AddMedia(collection, mediaItems);
                            }
                        }));
                    },
                });
            }

            items.Add(new SeparatorMenuFlyoutItemModel());
        }

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.NewCollection,
            Click = () =>
            {
                CoroutineUtils.Run(async () =>
                {
                    var collection = MediaModel.CreateCollection();
                    await collection.SetTitle1(StringResourceProvider.Instance.NewCollection);
                    var dialog = new EditMediaInfoDialog([collection]);
                    await dialog.ShowAsync(windowId);
                    if (collection.IsExternal)
                    {
                        return;
                    }

                    await BusyStateManager.WithBusyState(async () =>
                    {
                        await CollectionModel.AddMedia(collection, mediaItems);

                        // Set default cover
                        string? coverImage = MediaExt.GetCoverImageUri(collection);
                        if (string.IsNullOrEmpty(coverImage) && mediaItems.Count > 0)
                        {
                            MediaModel media = mediaItems[0];
                            collection.SetExt(MediaExt.COVER_IMAGE, ResourceUri.CreateMediaThumbnail(media.Id).ToString());
                            await collection.FlushExt();
                        }
                    });
                });
            },
        });

        return items;
    }

    private static List<BaseMenuFlyoutItemModel> CreateSendToWindowMenuItems(ActionHandler actionHandler, Route route)
    {
        List<BaseMenuFlyoutItemModel> items = [];

        int currentWindowId = -1;
        if (actionHandler.TryGetComponent(out IMainWindowComponent? mainWindowCom))
        {
            currentWindowId = mainWindowCom.WindowId;
        }

        Dictionary<int, string> windowInfo = App.Instance.WindowManager.GetAllWindowInfo();
        foreach (KeyValuePair<int, string> pair in windowInfo)
        {
            int windowId = pair.Key;
            if (windowId == currentWindowId)
            {
                continue;
            }

            string title = pair.Value;
            string name = StringResourceProvider.Instance.WindowN.Replace("$n", windowId.ToString());
            if (!string.IsNullOrEmpty(title))
            {
                name += $" ({title})";
            }

            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = name,
                Click = () =>
                {
                    ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                        .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                        .AddParameter(OpenTabProvider.PARAM_WINDOW_ID, windowId.ToString())
                        .Build();
                    actionHandler.HandleNoResult(actionModel);
                }
            });
        }

        items.Add(new SimpleMenuFlyoutItemModel()
        {
            Text = StringResourceProvider.Instance.NewWindow,
            Click = () =>
            {
                ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                    .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                    .AddParameter(OpenTabProvider.PARAM_WINDOW_ID, "-1")
                    .Build();
                actionHandler.HandleNoResult(actionModel);
            }
        });

        return items;
    }

    private static async Task<List<BaseMenuFlyoutItemModel>> CreateMediaLinkMenuItems(MediaModel media, ActionHandler actionHandler)
    {
        Dictionary<string, TagLinkModel.LinkModel> linkMap = [];

        foreach (KeyValuePair<string, MediaTagCategory> tagItem in media.Tags)
        {
            string tagCategory = tagItem.Key;
            TagCategoryInfoModel? tagCategoryInfo = await TagCategoryInfoModel.Get(tagCategory);
            List<TagLinkModel.LinkModel> tagCategoryLinks = tagCategoryInfo is null ? [] :
                TagLinkModel.Parse(tagCategoryInfo.GetExt(TagCategoryInfoExt.LINKS)).Links;
            foreach (string tag in tagItem.Value.Tags)
            {
                foreach (TagLinkModel.LinkModel item in tagCategoryLinks)
                {
                    item.Name = $"{item.Name} ({tag})";
                    item.ReplaceTagVariables(tagCategory, tag);
                    linkMap[item.Name] = item;
                }

                TagInfoModel? tagInfo = await TagInfoModel.Get(tagCategory, tag);
                if (tagInfo is not null)
                {
                    var linkModel = TagLinkModel.Parse(tagInfo.GetExt(TagInfoExt.LINKS));
                    foreach (TagLinkModel.LinkModel item in linkModel.Links)
                    {
                        item.Name = $"{item.Name} ({tag})";
                        item.ReplaceTagVariables(tagCategory, tag);
                        linkMap[item.Name] = item;
                    }
                }
            }
        }

        {
            string? linkJson = media.GetExt(MediaExt.LINKS);
            var linkModel = TagLinkModel.Parse(linkJson);
            foreach (TagLinkModel.LinkModel item in linkModel.Links)
            {
                linkMap[item.Name] = item;
            }
        }

        List<TagLinkModel.LinkModel> links = [.. linkMap.Values];
        links.Sort((a, b) => a.Name.CompareTo(b.Name));
        return CreateLinkMenuItems(actionHandler, links);
    }

    private static List<BaseMenuFlyoutItemModel> CreateLinkMenuItems(ActionHandler actionHandler, List<TagLinkModel.LinkModel> links)
    {
        List<BaseMenuFlyoutItemModel> items = [];
        if (links.Count > 0)
        {
            foreach (TagLinkModel.LinkModel link in links)
            {
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = link.Name,
                    Click = () =>
                    {
                        if (StringUtils.TryNormalizeWebUrl(link.Link, out Uri? uri))
                        {
                            CoroutineUtils.Run(async () => await Windows.System.Launcher.LaunchUriAsync(uri));
                        }
                        else
                        {
                            ActionModel actionModel = ActionModel.Builder.Create(MessageDialogProvider.NAME)
                                .AddParameter(MessageDialogProvider.PARAM_TITLE, StringResourceProvider.Instance.LinkErrorTitle)
                                .AddParameter(MessageDialogProvider.PARAM_MESSAGE, StringResourceProvider.Instance.LinkErrorContent.Replace("$link", link.Link))
                                .Build();
                            actionHandler.HandleNoResult(actionModel);
                        }
                    }
                });
            }
        }
        else
        {
            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.None,
                IsEnabled = false,
            });
        }

        return items;
    }

    private static List<BaseMenuFlyoutItemModel> CreateMediaTagMenuItems(MediaModel media, ActionHandler actionHandler)
    {
        List<BaseMenuFlyoutItemModel> items = [];
        var tags = media.Tags
            .SelectMany(item => item.Value.Tags.Select(tag => (Category: item.Key, Tag: tag)))
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Tag)
            .ToList();
        if (tags.Count > 0)
        {
            foreach ((string category, string tag) in tags)
            {
                string name = $"{tag} ({category})";
                items.Add(new SimpleMenuFlyoutItemModel()
                {
                    Text = name,
                    Click = () =>
                    {
                        string expression = $"%{MediaSQLProviderUtils.VAR_TAG}.\"{ExpressionUtils.EscapeString(category)}\"=\"{ExpressionUtils.EscapeString(tag)}\"";
                        Route route = Route.Create(RouterConstants.SCHEME_APP + RouterConstants.HOST_SEARCH)
                            .WithParam(RouterConstants.ARG_KEYWORD, $"exp:\"{ExpressionUtils.EscapeString(expression)}\"");
                        ActionModel actionModel = ActionModel.Builder.Create(OpenTabProvider.NAME)
                            .AddParameter(OpenTabProvider.PARAM_URL, route.Url)
                            .AddParameter(OpenTabProvider.PARAM_TAB_ID, string.Empty)
                            .Build();
                        actionHandler.HandleNoResult(actionModel);
                    }
                });
            }
        }
        else
        {
            items.Add(new SimpleMenuFlyoutItemModel()
            {
                Text = StringResourceProvider.Instance.None,
                IsEnabled = false,
            });
        }

        return items;
    }

    private static string TrimText(string text)
    {
        const int maxLength = 30;

        if (text.Length <= maxLength)
        {
            return text;
        }

        int count = maxLength / 2 - 1;
        return $"{text[..count]} ... {text[^count..]}";
    }
}
