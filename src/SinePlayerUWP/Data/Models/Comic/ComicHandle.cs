// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using SinePlayerUWP.Common.ErrorHandling;
using SinePlayerUWP.Common.Localization;
using SinePlayerUWP.Common.Utils;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Common.Utils;
using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Models.Comic;

internal abstract partial class ComicHandle(MediaType type)
{
    //
    // Constants
    //

    private const string TAG = nameof(ComicHandle);

    //
    // Static Methods
    //

    public static ICondition CreateComicOnlyCondition()
    {
        return new ComparisonCondition(
            ColumnOrValue.FromColumn(MediaTable.ColumnType),
            ColumnOrValue.FromValue((long)MediaType.Collection),
            ComparisonCondition.TypeEnum.NotEqual);
    }

    public static Task Enqueue(Action action)
    {
        return SqliteDB.MediaDatabaseDispatcher.Submit(action);
    }

    public static Task<T> Enqueue<T>(Func<T> func)
    {
        return SqliteDB.MediaDatabaseDispatcher.Submit(func);
    }

    public static long InsertNoLock(MediaType type, string location)
    {
        ComicHandle? comic = FromType(type);
        if (comic is null)
        {
            return -1;
        }

        comic.Location = location;
        comic.SetAsDefaultInfo();
        comic.InsertNewNoLock();
        return comic.Id;
    }

    public static Task<ComicHandle?> FromId(long id)
    {
        return Enqueue(() =>
        {
            return FromIdNoLock(id);
        });
    }

    public static Task<ComicHandle?> FromLocation(string location)
    {
        return Enqueue(() =>
        {
            return FromLocationNoLock(location);
        });
    }

    public static Task<List<ComicHandle>> BatchFromId(IEnumerable<long> ids)
    {
        return Enqueue(() =>
        {
            return BatchFromIdNoLock(ids);
        });
    }

    private static ComicHandle? FromIdNoLock(long id)
    {
        List<ComicHandle> result = BatchFromIdNoLock([id]);
        if (result.Count == 0)
        {
            return null;
        }

        return result[0];
    }

    private static List<ComicHandle> BatchFromIdNoLock(IEnumerable<long> ids)
    {
        {
            bool isEmpty = true;
            foreach (long _ in ids)
            {
                isEmpty = false;
                break;
            }
            if (isEmpty)
            {
                return [];
            }
        }

        Dictionary<long, ComicHandle> comics = new(ids.Count());
        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
        {
            SelectCommand command = SelectCommand.Create(MediaTable.Instance)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), idChunk));
            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
            IReaderToken<long> typeToken = command.PutQueryInt64(MediaTable.ColumnType);
            IReaderToken<string> locationToken = command.PutQueryString(MediaTable.ColumnLocation);
            IReaderToken<string> title1Token = command.PutQueryString(MediaTable.ColumnTitle1);
            IReaderToken<string> title2Token = command.PutQueryString(MediaTable.ColumnTitle2);
            IReaderToken<bool> hiddenToken = command.PutQueryBoolean(MediaTable.ColumnHidden);
            IReaderToken<int> ratingToken = command.PutQueryInt32(MediaTable.ColumnRating);
            IReaderToken<int> progressToken = command.PutQueryInt32(MediaTable.ColumnProgress);
            IReaderToken<DateTimeOffset> lastVisitToken = command.PutQueryDateTimeOffset(MediaTable.ColumnLastVisit);
            IReaderToken<double> lastPositionToken = command.PutQueryDouble(MediaTable.ColumnLastPosition);
            IReaderToken<string> descriptionToken = command.PutQueryString(MediaTable.ColumnDescription);
            IReaderToken<int> completionStatusToken = command.PutQueryInt32(MediaTable.ColumnCompletionStatus);
            IReaderToken<int> pageCountToken = command.PutQueryInt32(MediaTable.ColumnPageCount);
            IReaderToken<string> extToken = command.PutQueryString(MediaTable.ColumnExt);
            using SelectCommand.IReader reader = command.Execute();

            while (reader.Read())
            {
                long id = idToken.GetValue();
                var type = (MediaType)typeToken.GetValue();
                string location = locationToken.GetValue();
                string title1 = title1Token.GetValue();
                string title2 = title2Token.GetValue();
                bool isHidden = hiddenToken.GetValue();
                int rating = ratingToken.GetValue();
                int progress = progressToken.GetValue();
                DateTimeOffset lastVisit = lastVisitToken.GetValue();
                double lastPosition = lastPositionToken.GetValue();
                string description = descriptionToken.GetValue();
                CompletionStatusEnum completionStatus = ParseCompletionStatus(completionStatusToken.GetValue());
                int pageCount = pageCountToken.GetValue();
                string extJson = extToken.GetValue();

                ComicHandle? comic = FromType(type);
                if (comic is null)
                {
                    continue;
                }

                comic.Id = id;
                comic.Location = location;
                comic.Title1 = title1;
                comic.Title2 = title2;
                comic.IsHidden = isHidden;
                comic.Rating = rating;
                comic.Progress = progress;
                comic.LastVisit = lastVisit;
                comic.LastPosition = lastPosition;
                comic.Description = description;
                comic._tags = new([]);
                comic.CompletionStatus = completionStatus;
                comic.PageCount = pageCount;

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
                            comic._ext[pair.Key] = pair.Value;
                        }
                    }
                }

                comics[id] = comic;
            }
        }

        Dictionary<long, TagTempData> tagCategories = new(comics.Count);
        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(comics.Keys))
        {
            SelectCommand command = SelectCommand.Create(MediaTagCategoryTable.Instance)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagCategoryTable.ColumnMediaId), idChunk));
            IReaderToken<long> comicIdToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnMediaId);
            IReaderToken<long> tagCategoryIdToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnId);
            IReaderToken<string> nameToken = command.PutQueryString(MediaTagCategoryTable.ColumnName);
            using SelectCommand.IReader reader = command.Execute();

            while (reader.Read())
            {
                long comicId = comicIdToken.GetValue();
                if (comics.ContainsKey(comicId))
                {
                    long tagCategoryId = tagCategoryIdToken.GetValue();
                    string name = nameToken.GetValue();
                    var tagData = new TagTempData
                    {
                        Name = name,
                        ComicId = comicId,
                    };
                    tagCategories[tagCategoryId] = tagData;
                }
            }
        }

        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(tagCategories.Keys))
        {
            SelectCommand command = SelectCommand.Create(MediaTagTable.Instance)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagTable.ColumnTagCategoryId), idChunk));
            IReaderToken<long> tagCategoryIdToken = command.PutQueryInt64(MediaTagTable.ColumnTagCategoryId);
            IReaderToken<string> tagToken = command.PutQueryString(MediaTagTable.ColumnContent);
            using SelectCommand.IReader reader = command.Execute();

            while (reader.Read())
            {
                long tagCategoryId = tagCategoryIdToken.GetValue();
                if (tagCategories.TryGetValue(tagCategoryId, out TagTempData? tagData))
                {
                    string tag = tagToken.GetValue();
                    tagData.Tags.Add(tag);
                }
            }
        }

        {
            Dictionary<long, Dictionary<string, ComicTagCategory>> comicTags = [];
            foreach (TagTempData tagCategory in tagCategories.Values)
            {
                if (comics.TryGetValue(tagCategory.ComicId, out _))
                {
                    if (!comicTags.TryGetValue(tagCategory.ComicId, out Dictionary<string, ComicTagCategory>? tags))
                    {
                        tags = [];
                        comicTags[tagCategory.ComicId] = tags;
                    }

                    tags[tagCategory.Name] = new(tagCategory.Tags);
                }
            }

            foreach (KeyValuePair<long, Dictionary<string, ComicTagCategory>> pair in comicTags)
            {
                if (comics.TryGetValue(pair.Key, out ComicHandle? comic))
                {
                    comic._tags = new(pair.Value);
                }
            }
        }

        return [.. comics.Values];
    }

    private static ComicHandle? FromLocationNoLock(string location)
    {
        SelectCommand command = SelectCommand.Create(MediaTable.Instance)
            .AppendCondition(MediaTable.ColumnLocation, location)
            .AppendCondition(CreateComicOnlyCondition())
            .Limit(1);
        IReaderToken<long> comicIdToken = command.PutQueryInt64(MediaTable.ColumnId);
        using SelectCommand.IReader reader = command.Execute();

        if (!reader.Read())
        {
            return null;
        }

        long comicId = comicIdToken.GetValue();
        return FromIdNoLock(comicId);
    }

    private static ComicHandle? FromType(MediaType type)
    {
        switch (type)
        {
            case MediaType.Video:
            case MediaType.Audio:
                return new FileComicHandle(type);
            case MediaType.Collection:
                return new CollectionComicHandle();
            default:
                Logger.F(TAG, $"Unknown comic type: {type}");
                return null;
        }
    }

    private static CompletionStatusEnum ParseCompletionStatus(int value)
    {
        if (Enum.IsDefined(typeof(CompletionStatusEnum), value))
        {
            return (CompletionStatusEnum)value;
        }

        return CompletionStatusEnum.Unread;
    }

    //
    // Member variables
    //

    private ReadOnlyTags _tags = new([]);
    private readonly ConcurrentDictionary<string, string> _ext = [];

    //
    // Properties
    //

    public long Id { get; set; } = -1;
    public MediaType Type { get; } = type;
    public CompletionStatusEnum CompletionStatus { get; private set; }
    public string Location { get; protected set; } = string.Empty;
    public string Title1 { get; protected set; } = string.Empty;
    public string Title2 { get; protected set; } = string.Empty;
    public bool IsHidden { get; protected set; } = false;
    public int Rating { get; protected set; } = -1;
    public int Progress { get; protected set; } = -1;
    public DateTimeOffset LastVisit { get; protected set; } = DateTimeOffset.MinValue;
    public double LastPosition { get; protected set; } = 0.0;
    public string Description { get; private set; } = string.Empty;
    public IReadOnlyDictionary<string, ComicTagCategory> Tags => _tags;
    public IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory> TagsForPlugin => _tags;
    public int PageCount { get; set; } = -1;

    public abstract bool IsEditable { get; }
    public virtual string FileSystemPath => Location;
    public bool IsExternal => Id < 0;
    public bool IsCollection => Type == MediaType.Collection;

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

    public async Task Save()
    {
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                InsertNewNoLock();
            }
            else
            {
                var command = UpdateCommand.Create(MediaTable.Instance);
                foreach (IColumnTypeless column in _allNonIdColumns.Value)
                {
                    command.AppendColumn(column, GetColumnValue(column));
                }

                command.AppendCondition(MediaTable.ColumnId, Id).Execute();
            }
        });
    }

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
    }

    public async Task FlushExt()
    {
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnExt, GetColumnValue(MediaTable.ColumnExt))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetTitle1(string title)
    {
        Title1 = title;
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnTitle1, GetColumnValue(MediaTable.ColumnTitle1))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetTitle2(string title)
    {
        Title2 = title;
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnTitle2, GetColumnValue(MediaTable.ColumnTitle2))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetDescription(string description)
    {
        Description = description;
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnDescription, GetColumnValue(MediaTable.ColumnDescription))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetTags<T>(IEnumerable<KeyValuePair<string, T>> tags) where T : IEnumerable<string>
    {
        Dictionary<string, HashSet<string>> newTags = [];
        foreach (KeyValuePair<string, T> pair in tags)
        {
            string name = pair.Key.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            List<string> processedTags = [];
            foreach (string tag in pair.Value)
            {
                string processedTag = tag.Trim();
                if (processedTag.Length == 0)
                {
                    continue;
                }

                processedTags.Add(processedTag);
            }

            if (processedTags.Count == 0)
            {
                continue;
            }

            if (!newTags.TryGetValue(name, out HashSet<string>? existingTags))
            {
                existingTags = [];
                newTags.Add(name, existingTags);
            }

            foreach (string tag in processedTags)
            {
                existingTags.Add(tag);
            }
        }

        _tags = new(newTags.ToDictionary(p => p.Key, p => new ComicTagCategory(p.Value)));

        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            InternalSaveTagsNoLock();
        });
    }

    public async Task SetLocation(string location)
    {
        Location = location;
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnLocation, GetColumnValue(MediaTable.ColumnLocation))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetRating(int rating)
    {
        rating = Math.Clamp(rating, -1, 100);
        Rating = rating;
        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnRating, GetColumnValue(MediaTable.ColumnRating))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetProgress(int progress, double lastPosition)
    {
        Progress = Math.Clamp(progress, -1, 100);
        LastPosition = lastPosition;

        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnProgress, GetColumnValue(MediaTable.ColumnProgress))
                .AppendColumn(MediaTable.ColumnLastPosition, GetColumnValue(MediaTable.ColumnLastPosition))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    //
    // Utilities
    //

    public async Task<bool> MoveToLocation(string newLocation)
    {
        return await MoveToLocationInternal(newLocation);
    }

    //
    // Virtual Methods
    //

    protected virtual Task<bool> MoveToLocationInternal(string newLocation)
    {
        return Task.FromResult(false);
    }

    //
    // Abstract Methods
    //

    public abstract IReadOnlyList<string> GetFolderViewPath();

    public abstract Task<ErrorResult<BaseComicConnection>> OpenComicConnection();

    //
    // DB Helpers
    //

    private static readonly Lazy<IReadOnlyList<IColumnTypeless>> _allNonIdColumns = new(() =>
    {
        return [
            MediaTable.ColumnType,
            MediaTable.ColumnLocation,
            MediaTable.ColumnTitle1,
            MediaTable.ColumnTitle2,
            MediaTable.ColumnHidden,
            MediaTable.ColumnRating,
            MediaTable.ColumnProgress,
            MediaTable.ColumnLastVisit,
            MediaTable.ColumnLastPosition,
            MediaTable.ColumnDescription,
            MediaTable.ColumnCompletionStatus,
            MediaTable.ColumnExt,
            MediaTable.ColumnPageCount,
        ];
    });

    private static readonly Lazy<IReadOnlyDictionary<string, Func<ComicHandle, object>>> _columnValueEvaluator = new(() =>
    {
        Dictionary<string, Func<ComicHandle, object>> evaluators = [];
        evaluators[MediaTable.ColumnId.Name] = i => TypeAssert.AssertLong(i.Id);
        evaluators[MediaTable.ColumnType.Name] = i => TypeAssert.AssertLong((long)i.Type);
        evaluators[MediaTable.ColumnLocation.Name] = i => TypeAssert.AssertString(i.Location);
        evaluators[MediaTable.ColumnTitle1.Name] = i => TypeAssert.AssertString(i.Title1);
        evaluators[MediaTable.ColumnTitle2.Name] = i => TypeAssert.AssertString(i.Title2);
        evaluators[MediaTable.ColumnHidden.Name] = i => TypeAssert.AssertBoolean(i.IsHidden);
        evaluators[MediaTable.ColumnRating.Name] = i => TypeAssert.AssertInt(i.Rating);
        evaluators[MediaTable.ColumnProgress.Name] = i => TypeAssert.AssertInt(i.Progress);
        evaluators[MediaTable.ColumnLastVisit.Name] = i => TypeAssert.AssertDateTimeOffset(i.LastVisit);
        evaluators[MediaTable.ColumnLastPosition.Name] = i => TypeAssert.AssertDouble(i.LastPosition);
        evaluators[MediaTable.ColumnDescription.Name] = i => TypeAssert.AssertString(i.Description);
        evaluators[MediaTable.ColumnCompletionStatus.Name] = i => TypeAssert.AssertInt((int)i.CompletionStatus);
        evaluators[MediaTable.ColumnExt.Name] = i => TypeAssert.AssertString(JsonSerializer.Serialize(i._ext));
        evaluators[MediaTable.ColumnPageCount.Name] = i => TypeAssert.AssertInt(i.PageCount);
        return evaluators;
    });

    public object GetColumnValue(IColumnTypeless column)
    {
        Func<ComicHandle, object> evaluator = _columnValueEvaluator.Value[column.Name];
        return evaluator(this);
    }

    private void InsertNewNoLock()
    {
        var command = InsertCommand.Create(MediaTable.Instance);
        foreach (IColumnTypeless column in _allNonIdColumns.Value)
        {
            command.AppendColumn(column, GetColumnValue(column));
        }

        Id = command.Execute();
        InternalSaveTagsNoLock();
    }

    //
    // Unsorted
    //

    public async Task SaveHiddenAsync(bool hidden)
    {
        IsHidden = hidden;

        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnHidden, GetColumnValue(MediaTable.ColumnHidden))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SaveCompletionStatus(CompletionStatusEnum completionState)
    {
        CompletionStatus = completionState;

        await Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnCompletionStatus, GetColumnValue(MediaTable.ColumnCompletionStatus))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public void SetAsVisited()
    {
        LastVisit = DateTimeOffset.Now;
        Progress = Math.Max(Progress, 0);

        CoroutineUtils.Run(() => Enqueue(() =>
        {
            if (IsExternal)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnProgress, GetColumnValue(MediaTable.ColumnProgress))
                .AppendColumn(MediaTable.ColumnLastVisit, GetColumnValue(MediaTable.ColumnLastVisit))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        }));
    }

    private void SetAsDefaultInfo()
    {
        List<string> subPaths = [Location];
        var tags = new List<string>();
        foreach (string path in subPaths)
        {
            List<string> subTags = [.. path.Split('\\')];
            if (subTags.Count == 0)
            {
                continue;
            }

            subTags[^1] = StringUtils.DisplayNameFromFilename(subTags[^1]);
            tags.AddRange(subTags);
        }

        Title1 = tags.Count >= 1 ? tags[^1] : string.Empty;
        Title2 = string.Empty;

        if (tags.Count >= 2)
        {
            _tags = new(new Dictionary<string, ComicTagCategory>
            {
                { StringResourceProvider.Instance.Default, new ComicTagCategory([.. tags[1..^1]]) }
            });
        }
        else
        {
            _tags = new([]);
        }
    }

    private void InternalSaveTagsNoLock()
    {
        DeleteCommand.Create(MediaTagCategoryTable.Instance)
            .AppendCondition(MediaTagCategoryTable.ColumnMediaId, Id)
            .Execute();

        foreach (KeyValuePair<string, ComicTagCategory> item in Tags)
        {
            long tagCategoryId = InsertCommand.Create(MediaTagCategoryTable.Instance)
                .AppendColumn(MediaTagCategoryTable.ColumnName, item.Key)
                .AppendColumn(MediaTagCategoryTable.ColumnMediaId, Id)
                .Execute();

            foreach (string tag in item.Value.Tags)
            {
                InsertCommand.Create(MediaTagTable.Instance)
                    .AppendColumn(MediaTagTable.ColumnContent, tag)
                    .AppendColumn(MediaTagTable.ColumnMediaId, Id)
                    .AppendColumn(MediaTagTable.ColumnTagCategoryId, tagCategoryId)
                    .Execute();
            }
        }
    }

    //
    // Types
    //

    private partial class ReadOnlyTags(Dictionary<string, ComicTagCategory> source) :
        IReadOnlyDictionary<string, ComicTagCategory>,
        IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>
    {
        ComicTagCategory IReadOnlyDictionary<string, ComicTagCategory>.this[string key] => source[key];

        SDK.Plugins.Comic.IComicTagCategory IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>.this[string key] => source[key];

        IEnumerable<string> IReadOnlyDictionary<string, ComicTagCategory>.Keys => source.Keys;

        IEnumerable<string> IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>.Keys => source.Keys;

        IEnumerable<ComicTagCategory> IReadOnlyDictionary<string, ComicTagCategory>.Values => source.Values;

        IEnumerable<SDK.Plugins.Comic.IComicTagCategory> IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>.Values => source.Values;

        int IReadOnlyCollection<KeyValuePair<string, ComicTagCategory>>.Count => source.Count;

        int IReadOnlyCollection<KeyValuePair<string, SDK.Plugins.Comic.IComicTagCategory>>.Count => source.Count;

        bool IReadOnlyDictionary<string, ComicTagCategory>.ContainsKey(string key)
        {
            return source.ContainsKey(key);
        }

        bool IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>.ContainsKey(string key)
        {
            return source.ContainsKey(key);
        }

        IEnumerator<KeyValuePair<string, ComicTagCategory>> IEnumerable<KeyValuePair<string, ComicTagCategory>>.GetEnumerator()
        {
            return source.GetEnumerator();
        }

        IEnumerator<KeyValuePair<string, SDK.Plugins.Comic.IComicTagCategory>> IEnumerable<KeyValuePair<string, SDK.Plugins.Comic.IComicTagCategory>>.GetEnumerator()
        {
            foreach ((string? key, ComicTagCategory? value) in source)
            {
                yield return new(key, value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return source.GetEnumerator();
        }

        bool IReadOnlyDictionary<string, ComicTagCategory>.TryGetValue(string key, [MaybeNullWhen(false)] out ComicTagCategory value)
        {
            return source.TryGetValue(key, out value);
        }

        bool IReadOnlyDictionary<string, SDK.Plugins.Comic.IComicTagCategory>.TryGetValue(string key, [MaybeNullWhen(false)] out SDK.Plugins.Comic.IComicTagCategory value)
        {
            bool result = source.TryGetValue(key, out ComicTagCategory? tempValue);
            value = tempValue;
            return result;
        }
    }

    private class TagTempData
    {
        public long ComicId = -1;
        public string Name = "";
        public HashSet<string> Tags = [];
    }
};
