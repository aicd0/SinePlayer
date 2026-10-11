// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SinePlayerUWP.Common.Misc;
using SinePlayerUWP.Core.Common.DebugTools;
using SinePlayerUWP.Core.Database.SqlHelpers;
using SinePlayerUWP.Data.Database;
using SinePlayerUWP.Data.Models.Media;
using SinePlayerUWP.Data.Tables;

namespace SinePlayerUWP.Data.Models.Misc;

internal static class CollectionModel
{
    private const string TAG = nameof(CollectionModel);

    //
    // Queries
    //

    public static async Task<IReadOnlyList<long>> GetAllCollectionIds()
    {
        return await MediaHandle.Enqueue(() =>
        {
            SelectCommand command = SelectCommand.Create(MediaTable.Instance)
                .AppendCondition(MediaTable.ColumnType, (long)MediaType.Collection);
            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
            List<long> results = [];
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                results.Add(idToken.GetValue());
            }
            return results;
        });
    }

    public static async Task<IReadOnlySet<long>> GetCollectionIdsContainingAll(IEnumerable<long> mediaIds)
    {
        List<long> ids = [.. mediaIds.Where(x => x >= 0).Distinct()];
        if (ids.Count == 0)
        {
            return new HashSet<long>();
        }

        return await MediaHandle.Enqueue(() =>
        {
            HashSet<long> result = [];
            bool hasResult = false;
            foreach (IEnumerable<long> chunk in SqlUtils.ChunkBy(ids))
            {
                List<long> chunkIds = [.. chunk];
                HashSet<long> matched = QueryCollectionIdsContainingAllNoLock(chunkIds);
                if (!hasResult)
                {
                    result = matched;
                    hasResult = true;
                }
                else
                {
                    result.IntersectWith(matched);
                }

                if (result.Count == 0)
                {
                    break;
                }
            }

            return result;
        });
    }

    public static ICondition CreateMemberCondition(long collectionId)
    {
        SelectCommand subquery = SelectCommand.Create(MediaCollectionTable.Instance)
            .AppendCondition(MediaCollectionTable.ColumnCollectionId, collectionId);
        _ = subquery.PutQueryInt64(MediaCollectionTable.ColumnMediaId);
        return new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), subquery);
    }

    //
    // Writes
    //

    public static async Task AddMedia(MediaModel collection, IEnumerable<MediaModel> mediaItems)
    {
        if (collection.IsExternal || !collection.IsCollection)
        {
            return;
        }

        long collectionId = collection.Id;
        List<long> ids = [.. mediaItems
            .Where(x => !x.IsExternal && !x.IsCollection)
            .Select(x => x.Id)
            .Distinct()];
        if (ids.Count == 0)
        {
            return;
        }

        int insertedCount = await MediaHandle.Enqueue(() =>
        {
            if (collection.IsExternal)
            {
                Logger.F(TAG, "AddMedia: Cannot add mediaItems to a collection which is not saved");
                return 0;
            }

            int inserted = 0;
            SqliteDB.MediaDatabase.WithTransaction(() =>
            {
                HashSet<long> existingIds = QueryLinkedMediaIdsNoLock(collectionId, ids);
                List<long> newIds = [.. ids.Where(x => !existingIds.Contains(x))];
                foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(newIds))
                {
                    foreach (long mediaId in idChunk)
                    {
                        InsertCommand.Create(MediaCollectionTable.Instance)
                            .AppendColumn(MediaCollectionTable.ColumnCollectionId, collectionId)
                            .AppendColumn(MediaCollectionTable.ColumnMediaId, mediaId)
                            .Execute();
                        inserted++;
                    }
                }
            });
            return inserted;
        });

        if (insertedCount > 0)
        {
            DispatchUpdateEvent([collectionId]);
        }
    }

    public static async Task RemoveMedia(MediaModel collection, IEnumerable<long> mediaIds)
    {
        long collectionId = collection.Id;
        List<long> ids = [.. mediaIds.Where(x => x >= 0).Distinct()];
        if (ids.Count == 0)
        {
            return;
        }

        int removedCount = await MediaHandle.Enqueue(() =>
        {
            if (collection.IsExternal)
            {
                Logger.F(TAG, "RemoveMedia: Cannot remove mediaItems from a collection which is not saved");
                return 0;
            }

            int removed = 0;
            SqliteDB.MediaDatabase.WithTransaction(() =>
            {
                foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(ids))
                {
                    removed += DeleteCommand.Create(MediaCollectionTable.Instance)
                        .AppendCondition(MediaCollectionTable.ColumnCollectionId, collectionId)
                        .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), idChunk))
                        .Execute();
                }
            });
            return removed;
        });

        if (removedCount > 0)
        {
            DispatchUpdateEvent([collectionId]);
        }
    }

    //
    // Private helpers
    //

    private static HashSet<long> QueryCollectionIdsContainingAllNoLock(List<long> mediaIds)
    {
        HashSet<long> collectionIds = [];
        SelectCommand command = SelectCommand.Create(MediaCollectionTable.Instance)
            .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), mediaIds))
            .GroupBy(MediaCollectionTable.ColumnCollectionId);
        IReaderToken<long> collectionIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnCollectionId);
        IReaderToken<long> matchedCountToken = command.PutQueryCountDistinct(MediaCollectionTable.ColumnMediaId);
        using SelectCommand.IReader reader = command.Execute();
        while (reader.Read())
        {
            if (matchedCountToken.GetValue() == mediaIds.Count)
            {
                collectionIds.Add(collectionIdToken.GetValue());
            }
        }
        return collectionIds;
    }

    private static HashSet<long> QueryLinkedMediaIdsNoLock(long collectionId, List<long> mediaIds)
    {
        HashSet<long> ids = [];
        foreach (IEnumerable<long> idChunk in SqlUtils.ChunkBy(mediaIds))
        {
            SelectCommand command = SelectCommand.Create(MediaCollectionTable.Instance)
                .AppendCondition(MediaCollectionTable.ColumnCollectionId, collectionId)
                .AppendCondition(new InCondition(ColumnOrValue.FromColumn(MediaCollectionTable.ColumnMediaId), idChunk));
            IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaCollectionTable.ColumnMediaId);
            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                ids.Add(mediaIdToken.GetValue());
            }
        }
        return ids;
    }

    private static void DispatchUpdateEvent(IEnumerable<long> collectionIds)
    {
        GlobalEvent.Instance.CollectionUpdated.Emit(collectionIds);
    }
}
