// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.TagInfo;

internal class TagInfoModel
{
    private const string TAG = nameof(TagInfoModel);

    //
    // Member variables
    //

    private readonly ConcurrentDictionary<string, string> _ext = [];

    //
    // Properties
    //

    public string TagCategory { get; private set; }
    public string Name { get; private set; }

    private string ValueExt => JsonSerializer.Serialize(_ext);

    private TagInfoModel(string tagCategory, string tag)
    {
        Name = tag;
        TagCategory = tagCategory;
    }

    //
    // Getters
    //

    public string? GetExt(string key)
    {
        if (_ext.TryGetValue(key, out string? value))
        {
            return value;
        }

        return null;
    }

    //
    // Setters
    //

    public void SetExt(string key, string? value)
    {
        if (value is null)
        {
            _ext.Remove(key, out _);
        }
        else
        {
            _ext[key] = value;
        }

        TagInfoDatabase.DispatchTagInfoUpdateEvents();
    }

    public async Task FlushExt()
    {
        await TagInfoDatabase.Enqueue(() =>
        {
            SaveNoLock(this);
            return true;
        });
    }

    //
    // Static variables
    //

    private static readonly ConcurrentWeakPool<Key, TagInfoModel> _cache = new();

    //
    // Static methods
    //

    public static void InjectPrivateMembers()
    {
        TagInfoDatabase.TagInfoDeleteTagCategoryCacheOnlyNoLock = DeleteTagCategoryCacheOnlyNoLock;
        TagInfoDatabase.TagInfoRenameTagCategoryCacheOnlyNoLock = RenameTagCategoryCacheOnlyNoLock;
    }

    public static async Task<TagInfoModel?> Get(string tagCategory, string tag)
    {
        Key key = new(tagCategory, tag);
        if (_cache.TryGetValue(key, out TagInfoModel? model))
        {
            return model;
        }

        return await TagInfoDatabase.Enqueue(() =>
        {
            if (_cache.TryGetValue(key, out TagInfoModel? model))
            {
                return model;
            }

            model = QueryNoLock(tagCategory, tag);
            if (model is not null)
            {
                _cache.Set(key, model);
                return model;
            }

            return null;
        });
    }

    public static async Task<TagInfoModel> GetOrCreate(string tagCategory, string tag)
    {
        Key key = new(tagCategory, tag);
        if (_cache.TryGetValue(key, out TagInfoModel? model))
        {
            return model;
        }

        return await TagInfoDatabase.Enqueue(() =>
        {
            if (_cache.TryGetValue(key, out TagInfoModel? model))
            {
                return model;
            }

            model = QueryNoLock(tagCategory, tag);
            if (model is null)
            {
                model = new(tagCategory, tag);
                _cache.Set(key, model);
                SaveNoLock(model);
            }
            else
            {
                _cache.Set(key, model);
            }

            return model;
        });
    }

    private static TagInfoModel? QueryNoLock(string tagCategory, string tag)
    {
        SelectCommand command = SelectCommand.Create(TagsTable.Instance)
            .AppendCondition(TagsTable.ColumnTagCategory, tagCategory)
            .AppendCondition(TagsTable.ColumnName, tag)
            .Limit(1);
        IReaderToken<string> extToken = command.PutQueryString(TagsTable.ColumnExt);
        using SelectCommand.IReader reader = command.Execute();

        bool hasRecord = false;
        string extJson = string.Empty;
        while (reader.Read())
        {
            hasRecord = true;
            extJson = extToken.GetValue();
        }

        if (!hasRecord)
        {
            return null;
        }

        TagInfoModel model = new(tagCategory, tag);

        if (!string.IsNullOrEmpty(extJson))
        {
            Dictionary<string, string>? ext = null;
            try
            {
                ext = JsonSerializer.Deserialize<Dictionary<string, string>>(extJson);
            }
            catch (Exception ex)
            {
                Logger.F(TAG, ex);
            }

            if (ext != null)
            {
                foreach (KeyValuePair<string, string> pair in ext)
                {
                    model._ext[pair.Key] = pair.Value;
                }
            }
        }

        return model;
    }

    private static void SaveNoLock(TagInfoModel model)
    {
        string valueExt = model.ValueExt;

        int rowsUpdated = UpdateCommand.Create(TagsTable.Instance)
            .AppendCondition(TagsTable.ColumnTagCategory, model.TagCategory)
            .AppendCondition(TagsTable.ColumnName, model.Name)
            .AppendColumn(TagsTable.ColumnExt, valueExt)
            .Execute();

        if (rowsUpdated == 0)
        {
            // Ensure tag category exists
            TagInfoDatabase.TagCategoryInfoGetOrCreateNoLock(model.TagCategory);

            InsertCommand.Create(TagsTable.Instance)
                .AppendColumn(TagsTable.ColumnTagCategory, model.TagCategory)
                .AppendColumn(TagsTable.ColumnName, model.Name)
                .AppendColumn(TagsTable.ColumnExt, valueExt)
                .Execute();
        }
    }

    private static void DeleteNoLock(string tagCategory, string tag)
    {
        Key key = new(tagCategory, tag);
        _cache.TryRemove(key, out _);

        DeleteCommand.Create(TagsTable.Instance)
            .AppendCondition(TagsTable.ColumnTagCategory, tagCategory)
            .AppendCondition(TagsTable.ColumnName, tag)
            .Execute();
    }

    private static void DeleteTagCategoryCacheOnlyNoLock(string tagCategory)
    {
        List<string> tags = [];
        SelectCommand command = SelectCommand.Create(TagsTable.Instance)
            .AppendCondition(TagsTable.ColumnTagCategory, tagCategory);
        IReaderToken<string> tagToken = command.PutQueryString(TagsTable.ColumnName);
        using SelectCommand.IReader reader = command.Execute();
        while (reader.Read())
        {
            string tag = tagToken.GetValue();
            tags.Add(tag);
        }

        foreach (string tag in tags)
        {
            Key key = new(tagCategory, tag);
            _cache.TryRemove(key, out _);
        }
    }

    private static void RenameTagCategoryCacheOnlyNoLock(string oldName, string newName)
    {
        List<string> tags = [];
        SelectCommand command = SelectCommand.Create(TagsTable.Instance)
            .AppendCondition(TagsTable.ColumnTagCategory, oldName);
        IReaderToken<string> tagToken = command.PutQueryString(TagsTable.ColumnName);
        using SelectCommand.IReader reader = command.Execute();
        while (reader.Read())
        {
            string tag = tagToken.GetValue();
            tags.Add(tag);
        }

        foreach (string tag in tags)
        {
            Key oldKey = new(oldName, tag);
            Key newKey = new(newName, tag);
            if (_cache.TryRemove(oldKey, out TagInfoModel? tagInfoModel))
            {
                tagInfoModel.TagCategory = newName;
                _cache.Set(newKey, tagInfoModel);
            }
        }
    }

    //
    // Utilities
    //

    public static async Task Delete(string tagCategory, string tag)
    {
        await TagInfoDatabase.Enqueue(() =>
        {
            DeleteNoLock(tagCategory, tag);
            return true;
        });

        HashSet<long> mediaIds = [];
        await MediaHandle.Enqueue(() =>
        {
            SelectCommand subQuery = SelectCommand.Create(MediaTagCategoryTable.Instance)
                .AppendCondition(MediaTagCategoryTable.ColumnName, tagCategory);
            subQuery.PutQueryInt64(MediaTagCategoryTable.ColumnId);
            SelectCommand command = SelectCommand.Create(MediaTagTable.Instance)
                .AppendCondition(MediaTagTable.ColumnContent, tag)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnTagCategoryId), subQuery));
            IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaTagTable.ColumnMediaId);
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                long mediaId = mediaIdToken.GetValue();
                mediaIds.Add(mediaId);
            }

            return true;
        });

        List<Task> tasks = [];
        List<MediaModel> mediaItems = await MediaModel.BatchFromId(mediaIds);
        foreach (MediaModel media in mediaItems)
        {
            Dictionary<string, HashSet<string>> mediaTags = media.TagsCopy;
            if (mediaTags.TryGetValue(tagCategory, out HashSet<string>? tags))
            {
                tags.Remove(tag);
                tasks.Add(media.SetTags(mediaTags));
            }
        }

        await Task.WhenAll(tasks);
        TagInfoDatabase.DispatchTagInfoUpdateEvents();
    }

    public static async Task Rename(string oldTagCategory, string oldTag, string newTagCategory, string newTag)
    {
        await TagInfoDatabase.Enqueue(() =>
        {
            DeleteNoLock(newTagCategory, newTag);

            if (_cache.TryRemove(new Key(oldTagCategory, oldTag), out TagInfoModel? tagInfoModel))
            {
                tagInfoModel.TagCategory = newTagCategory;
                tagInfoModel.Name = newTag;
                _cache.Set(new Key(newTagCategory, newTag), tagInfoModel);
            }

            TagInfoDatabase.TagCategoryInfoGetOrCreateNoLock(newTagCategory);
            UpdateCommand.Create(TagsTable.Instance)
                .AppendColumn(TagsTable.ColumnTagCategory, newTagCategory)
                .AppendColumn(TagsTable.ColumnName, newTag)
                .AppendCondition(TagsTable.ColumnTagCategory, oldTagCategory)
                .AppendCondition(TagsTable.ColumnName, oldTag)
                .Execute();
            return true;
        });

        List<long> mediaIds = [];
        await MediaHandle.Enqueue(() =>
        {
            SelectCommand subQuery = SelectCommand.Create(MediaTagCategoryTable.Instance)
                .AppendCondition(MediaTagCategoryTable.ColumnName, oldTagCategory);
            subQuery.PutQueryInt64(MediaTagCategoryTable.ColumnId);
            SelectCommand command = SelectCommand.Create(MediaTagTable.Instance)
                .AppendCondition(MediaTagTable.ColumnContent, oldTag)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnTagCategoryId), subQuery));
            IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaTagTable.ColumnMediaId);
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                long mediaId = mediaIdToken.GetValue();
                mediaIds.Add(mediaId);
            }

            return true;
        });

        List<Task> tasks = [];
        List<MediaModel> mediaItems = await MediaModel.BatchFromId(mediaIds);
        foreach (MediaModel media in mediaItems)
        {
            Dictionary<string, HashSet<string>> mediaTags = media.TagsCopy;
            if (mediaTags.TryGetValue(oldTagCategory, out HashSet<string>? tags))
            {
                tags.Remove(oldTag);
                if (!mediaTags.TryGetValue(newTagCategory, out HashSet<string>? newTags))
                {
                    newTags = [];
                    mediaTags[newTagCategory] = newTags;
                }

                newTags.Add(newTag);
                tasks.Add(media.SetTags(mediaTags));
            }
        }

        await Task.WhenAll(tasks);
        TagInfoDatabase.DispatchTagInfoUpdateEvents();
    }

    //
    // Types
    //

    private class Key(string tagCategory, string tag)
    {
        public string TagCategory { get; } = tagCategory;
        public string Tag { get; } = tag;

        public override bool Equals(object? obj)
        {
            if (obj is Key other)
            {
                return Tag == other.Tag && TagCategory == other.TagCategory;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Tag, TagCategory);
        }
    }
}
