// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using SinePlayer.Common.Localization;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;
using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.Media;

internal abstract partial class MediaHandle(MediaType type)
{
    //
    // Constants
    //

    private const string TAG = nameof(MediaHandle);

    //
    // Static Methods
    //

    public static ICondition CreateMediaOnlyCondition()
    {
        return new ComparisonCondition(
            ColumnOrValue.FromColumn(MediaTable.ColumnType),
            ColumnOrValue.FromValue((long)MediaType.Collection),
            ComparisonCondition.TypeEnum.NotEqual);
    }

    public static ICondition CreateNonExternalCondition()
    {
        return new ComparisonCondition(
            ColumnOrValue.FromColumn(MediaTable.ColumnExternal),
            ColumnOrValue.FromValue(false));
    }

    public static ICondition CreateLibraryOnlyCondition()
    {
        return new AndCondition([
            CreateMediaOnlyCondition(),
            CreateNonExternalCondition(),
        ]);
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
        MediaHandle? media = FromType(type);
        if (media is null)
        {
            return -1;
        }

        media.Location = location;
        media.IsExternal = false;
        media.SetAsDefaultInfo();
        media.InsertNewNoLock();
        return media.Id;
    }

    public static Task<MediaHandle?> FromLocationOrInsertExternal(MediaType type, string location)
    {
        return Enqueue(() =>
        {
            MediaHandle? existing = FromLocationNoLock(location);
            if (existing is not null)
            {
                return existing;
            }

            MediaHandle? media = FromType(type);
            if (media is null)
            {
                return null;
            }

            media.Location = location;
            media.IsExternal = true;
            media.SetAsDefaultInfo();
            media.InsertNewNoLock();
            return media;
        });
    }

    public static Task<MediaHandle?> FromId(long id)
    {
        return Enqueue(() =>
        {
            return FromIdNoLock(id);
        });
    }

    public static Task<List<MediaHandle>> BatchFromId(IEnumerable<long> ids)
    {
        return Enqueue(() =>
        {
            return BatchFromIdNoLock(ids);
        });
    }

    private static MediaHandle? FromIdNoLock(long id)
    {
        List<MediaHandle> result = BatchFromIdNoLock([id]);
        if (result.Count == 0)
        {
            return null;
        }

        return result[0];
    }

    private static List<MediaHandle> BatchFromIdNoLock(IEnumerable<long> ids)
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

        Dictionary<long, MediaHandle> mediaItems = new(ids.Count());
        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
        {
            SelectCommand command = SelectCommand.Create(MediaTable.Instance)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), idChunk));
            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
            IReaderToken<long> typeToken = command.PutQueryInt64(MediaTable.ColumnType);
            IReaderToken<string> locationToken = command.PutQueryString(MediaTable.ColumnLocation);
            IReaderToken<bool> externalToken = command.PutQueryBoolean(MediaTable.ColumnExternal);
            IReaderToken<string> title1Token = command.PutQueryString(MediaTable.ColumnTitle1);
            IReaderToken<string> title2Token = command.PutQueryString(MediaTable.ColumnTitle2);
            IReaderToken<bool> hiddenToken = command.PutQueryBoolean(MediaTable.ColumnHidden);
            IReaderToken<int> ratingToken = command.PutQueryInt32(MediaTable.ColumnRating);
            IReaderToken<int> progressToken = command.PutQueryInt32(MediaTable.ColumnProgress);
            IReaderToken<DateTimeOffset> lastVisitToken = command.PutQueryDateTimeOffset(MediaTable.ColumnLastVisit);
            IReaderToken<long> lastPositionToken = command.PutQueryInt64(MediaTable.ColumnLastPositionMs);
            IReaderToken<string> descriptionToken = command.PutQueryString(MediaTable.ColumnDescription);
            IReaderToken<int> completionStatusToken = command.PutQueryInt32(MediaTable.ColumnCompletionStatus);
            IReaderToken<long> durationToken = command.PutQueryInt64(MediaTable.ColumnDurationMs);
            IReaderToken<string> extToken = command.PutQueryString(MediaTable.ColumnExt);
            using SelectCommand.IReader reader = command.Execute();

            while (reader.Read())
            {
                long id = idToken.GetValue();
                var type = (MediaType)typeToken.GetValue();
                string location = locationToken.GetValue();
                bool isExternal = externalToken.GetValue();
                string title1 = title1Token.GetValue();
                string title2 = title2Token.GetValue();
                bool isHidden = hiddenToken.GetValue();
                int rating = ratingToken.GetValue();
                int progress = progressToken.GetValue();
                DateTimeOffset lastVisit = lastVisitToken.GetValue();
                long lastPositionMs = lastPositionToken.GetValue();
                string description = descriptionToken.GetValue();
                CompletionStatusEnum completionStatus = ParseCompletionStatus(completionStatusToken.GetValue());
                long durationMs = durationToken.GetValue();
                string extJson = extToken.GetValue();

                MediaHandle? media = FromType(type);
                if (media is null)
                {
                    continue;
                }

                media.Id = id;
                media.Location = location;
                media.IsExternal = isExternal;
                media.Title1 = title1;
                media.Title2 = title2;
                media.IsHidden = isHidden;
                media.Rating = rating;
                media.Progress = progress;
                media.LastVisit = lastVisit;
                media.LastPositionMs = lastPositionMs;
                media.Description = description;
                media._tags = new([]);
                media.CompletionStatus = completionStatus;
                media.DurationMs = durationMs;

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
                            media._ext[pair.Key] = pair.Value;
                        }
                    }
                }

                mediaItems[id] = media;
            }
        }

        Dictionary<long, TagTempData> tagCategories = new(mediaItems.Count);
        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(mediaItems.Keys))
        {
            SelectCommand command = SelectCommand.Create(MediaTagCategoryTable.Instance)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaTagCategoryTable.ColumnMediaId), idChunk));
            IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnMediaId);
            IReaderToken<long> tagCategoryIdToken = command.PutQueryInt64(MediaTagCategoryTable.ColumnId);
            IReaderToken<string> nameToken = command.PutQueryString(MediaTagCategoryTable.ColumnName);
            using SelectCommand.IReader reader = command.Execute();

            while (reader.Read())
            {
                long mediaId = mediaIdToken.GetValue();
                if (mediaItems.ContainsKey(mediaId))
                {
                    long tagCategoryId = tagCategoryIdToken.GetValue();
                    string name = nameToken.GetValue();
                    var tagData = new TagTempData
                    {
                        Name = name,
                        MediaId = mediaId,
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
            Dictionary<long, Dictionary<string, MediaTagCategory>> mediaTags = [];
            foreach (TagTempData tagCategory in tagCategories.Values)
            {
                if (mediaItems.TryGetValue(tagCategory.MediaId, out _))
                {
                    if (!mediaTags.TryGetValue(tagCategory.MediaId, out Dictionary<string, MediaTagCategory>? tags))
                    {
                        tags = [];
                        mediaTags[tagCategory.MediaId] = tags;
                    }

                    tags[tagCategory.Name] = new(tagCategory.Tags);
                }
            }

            foreach (KeyValuePair<long, Dictionary<string, MediaTagCategory>> pair in mediaTags)
            {
                if (mediaItems.TryGetValue(pair.Key, out MediaHandle? media))
                {
                    media._tags = new(pair.Value);
                }
            }
        }

        return [.. mediaItems.Values];
    }

    private static MediaHandle? FromLocationNoLock(string location)
    {
        SelectCommand command = SelectCommand.Create(MediaTable.Instance)
            .AppendCondition(MediaTable.ColumnLocation, location)
            .AppendCondition(CreateMediaOnlyCondition())
            .Limit(1);
        IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaTable.ColumnId);
        using SelectCommand.IReader reader = command.Execute();

        if (!reader.Read())
        {
            return null;
        }

        long mediaId = mediaIdToken.GetValue();
        return FromIdNoLock(mediaId);
    }

    private static MediaHandle? FromType(MediaType type)
    {
        switch (type)
        {
            case MediaType.Video:
            case MediaType.Audio:
                return new FileMediaHandle(type);
            case MediaType.Collection:
                return new CollectionMediaHandle();
            default:
                Logger.F(TAG, $"Unknown media type: {type}");
                return null;
        }
    }

    private static CompletionStatusEnum ParseCompletionStatus(int value)
    {
        if (Enum.IsDefined(typeof(CompletionStatusEnum), value))
        {
            return (CompletionStatusEnum)value;
        }

        return CompletionStatusEnum.NotStarted;
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
    public bool IsExternal { get; set; }
    public MediaType Type { get; } = type;
    public CompletionStatusEnum CompletionStatus { get; private set; }
    public string Location { get; protected set; } = string.Empty;
    public string Title1 { get; protected set; } = string.Empty;
    public string Title2 { get; protected set; } = string.Empty;
    public bool IsHidden { get; set; } = false;
    public int Rating { get; protected set; } = -1;
    public int Progress { get; protected set; } = -1;
    public DateTimeOffset LastVisit { get; protected set; } = DateTimeOffset.MinValue;
    public long LastPositionMs { get; protected set; } = 0;
    public string Description { get; private set; } = string.Empty;
    public IReadOnlyDictionary<string, MediaTagCategory> Tags => _tags;
    public IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory> TagsForPlugin => _tags;
    public long DurationMs { get; set; } = -1;

    public virtual string FileSystemPath => Location;

    public bool IsPersistent => Id >= 0;

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
            if (!IsPersistent)
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
            if (!IsPersistent)
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
            if (!IsPersistent)
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
            if (!IsPersistent)
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
            if (!IsPersistent)
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

        _tags = new(newTags.ToDictionary(p => p.Key, p => new MediaTagCategory(p.Value)));

        await Enqueue(() =>
        {
            if (!IsPersistent)
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
            if (!IsPersistent)
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
            if (!IsPersistent)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnRating, GetColumnValue(MediaTable.ColumnRating))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    public async Task SetProgress(int progress, long lastPositionMs)
    {
        Progress = Math.Clamp(progress, -1, 100);
        LastPositionMs = lastPositionMs;

        await Enqueue(() =>
        {
            if (!IsPersistent)
            {
                return;
            }

            UpdateCommand.Create(MediaTable.Instance)
                .AppendColumn(MediaTable.ColumnProgress, GetColumnValue(MediaTable.ColumnProgress))
                .AppendColumn(MediaTable.ColumnLastPositionMs, GetColumnValue(MediaTable.ColumnLastPositionMs))
                .AppendCondition(MediaTable.ColumnId, Id)
                .Execute();
        });
    }

    //
    // Abstract Methods
    //

    public abstract IReadOnlyList<string> GetFolderViewPath();

    //
    // DB Helpers
    //

    private static readonly Lazy<IReadOnlyList<IColumnTypeless>> _allNonIdColumns = new(() =>
    {
        return [
            MediaTable.ColumnType,
            MediaTable.ColumnLocation,
            MediaTable.ColumnExternal,
            MediaTable.ColumnTitle1,
            MediaTable.ColumnTitle2,
            MediaTable.ColumnHidden,
            MediaTable.ColumnRating,
            MediaTable.ColumnProgress,
            MediaTable.ColumnLastVisit,
            MediaTable.ColumnLastPositionMs,
            MediaTable.ColumnDescription,
            MediaTable.ColumnCompletionStatus,
            MediaTable.ColumnExt,
            MediaTable.ColumnDurationMs,
        ];
    });

    private static readonly Lazy<IReadOnlyDictionary<string, Func<MediaHandle, object>>> _columnValueEvaluator = new(() =>
    {
        Dictionary<string, Func<MediaHandle, object>> evaluators = [];
        evaluators[MediaTable.ColumnId.Name] = i => TypeAssert.AssertLong(i.Id);
        evaluators[MediaTable.ColumnType.Name] = i => TypeAssert.AssertLong((long)i.Type);
        evaluators[MediaTable.ColumnLocation.Name] = i => TypeAssert.AssertString(i.Location);
        evaluators[MediaTable.ColumnExternal.Name] = i => TypeAssert.AssertBoolean(i.IsExternal);
        evaluators[MediaTable.ColumnTitle1.Name] = i => TypeAssert.AssertString(i.Title1);
        evaluators[MediaTable.ColumnTitle2.Name] = i => TypeAssert.AssertString(i.Title2);
        evaluators[MediaTable.ColumnHidden.Name] = i => TypeAssert.AssertBoolean(i.IsHidden);
        evaluators[MediaTable.ColumnRating.Name] = i => TypeAssert.AssertInt(i.Rating);
        evaluators[MediaTable.ColumnProgress.Name] = i => TypeAssert.AssertInt(i.Progress);
        evaluators[MediaTable.ColumnLastVisit.Name] = i => TypeAssert.AssertDateTimeOffset(i.LastVisit);
        evaluators[MediaTable.ColumnLastPositionMs.Name] = i => TypeAssert.AssertLong(i.LastPositionMs);
        evaluators[MediaTable.ColumnDescription.Name] = i => TypeAssert.AssertString(i.Description);
        evaluators[MediaTable.ColumnCompletionStatus.Name] = i => TypeAssert.AssertInt((int)i.CompletionStatus);
        evaluators[MediaTable.ColumnExt.Name] = i => TypeAssert.AssertString(JsonSerializer.Serialize(i._ext));
        evaluators[MediaTable.ColumnDurationMs.Name] = i => TypeAssert.AssertLong(i.DurationMs);
        return evaluators;
    });

    public object GetColumnValue(IColumnTypeless column)
    {
        Func<MediaHandle, object> evaluator = _columnValueEvaluator.Value[column.Name];
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
        if (IsExternal)
        {
            return;
        }

        IsHidden = hidden;

        await Enqueue(() =>
        {
            if (!IsPersistent || IsExternal)
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
            if (!IsPersistent)
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
            if (!IsPersistent)
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
            _tags = new(new Dictionary<string, MediaTagCategory>
            {
                { StringResourceProvider.Instance.Default, new MediaTagCategory([.. tags[1..^1]]) }
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

        foreach (KeyValuePair<string, MediaTagCategory> item in Tags)
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

    private partial class ReadOnlyTags(Dictionary<string, MediaTagCategory> source) :
        IReadOnlyDictionary<string, MediaTagCategory>,
        IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>
    {
        MediaTagCategory IReadOnlyDictionary<string, MediaTagCategory>.this[string key] => source[key];

        SDK.Plugins.Media.IMediaTagCategory IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>.this[string key] => source[key];

        IEnumerable<string> IReadOnlyDictionary<string, MediaTagCategory>.Keys => source.Keys;

        IEnumerable<string> IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>.Keys => source.Keys;

        IEnumerable<MediaTagCategory> IReadOnlyDictionary<string, MediaTagCategory>.Values => source.Values;

        IEnumerable<SDK.Plugins.Media.IMediaTagCategory> IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>.Values => source.Values;

        int IReadOnlyCollection<KeyValuePair<string, MediaTagCategory>>.Count => source.Count;

        int IReadOnlyCollection<KeyValuePair<string, SDK.Plugins.Media.IMediaTagCategory>>.Count => source.Count;

        bool IReadOnlyDictionary<string, MediaTagCategory>.ContainsKey(string key)
        {
            return source.ContainsKey(key);
        }

        bool IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>.ContainsKey(string key)
        {
            return source.ContainsKey(key);
        }

        IEnumerator<KeyValuePair<string, MediaTagCategory>> IEnumerable<KeyValuePair<string, MediaTagCategory>>.GetEnumerator()
        {
            return source.GetEnumerator();
        }

        IEnumerator<KeyValuePair<string, SDK.Plugins.Media.IMediaTagCategory>> IEnumerable<KeyValuePair<string, SDK.Plugins.Media.IMediaTagCategory>>.GetEnumerator()
        {
            foreach ((string? key, MediaTagCategory? value) in source)
            {
                yield return new(key, value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return source.GetEnumerator();
        }

        bool IReadOnlyDictionary<string, MediaTagCategory>.TryGetValue(string key, [MaybeNullWhen(false)] out MediaTagCategory value)
        {
            return source.TryGetValue(key, out value);
        }

        bool IReadOnlyDictionary<string, SDK.Plugins.Media.IMediaTagCategory>.TryGetValue(string key, [MaybeNullWhen(false)] out SDK.Plugins.Media.IMediaTagCategory value)
        {
            bool result = source.TryGetValue(key, out MediaTagCategory? tempValue);
            value = tempValue;
            return result;
        }
    }

    private class TagTempData
    {
        public long MediaId = -1;
        public string Name = "";
        public HashSet<string> Tags = [];
    }
};
