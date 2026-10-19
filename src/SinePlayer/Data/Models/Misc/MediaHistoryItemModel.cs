// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using SinePlayer.Common.Misc;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Database;
using SinePlayer.Data.Tables;

namespace SinePlayer.Data.Models.Misc;

internal class MediaHistoryItemModel
{
    //
    // Properties
    //

    public long MediaId { get; private set; }
    public string Title { get; private set; }
    public DateTimeOffset DateTime { get; protected set; }

    private MediaHistoryItemModel(long mediaId, string title, DateTimeOffset dateTime)
    {
        MediaId = mediaId;
        Title = title;
        DateTime = dateTime;
    }

    //
    // Getters
    //

    public static async Task<List<MediaHistoryItemModel>> GetAllAsync()
    {
        return await Enqueue(() =>
        {
            var command = SelectCommand.Create(MediaHistoryTable.Instance);
            IReaderToken<long> mediaIdToken = command.PutQueryInt64(MediaHistoryTable.ColumnMediaId);
            IReaderToken<string> titleToken = command.PutQueryString(MediaHistoryTable.ColumnTitle);
            IReaderToken<long> timeToken = command.PutQueryInt64(MediaHistoryTable.ColumnTime);
            using SelectCommand.IReader reader = command.Execute();
            List<MediaHistoryItemModel> items = [];
            while (reader.Read())
            {
                long mediaId = mediaIdToken.GetValue();
                string title = titleToken.GetValue();
                long time = timeToken.GetValue();
                MediaHistoryItemModel item = new(mediaId, title, DateTimeOffset.FromUnixTimeMilliseconds(time));
                items.Add(item);
            }

            return items;
        });
    }

    public static async Task<bool> IsEmptyAsync()
    {
        return await Enqueue(() =>
        {
            SelectCommand command = SelectCommand.Create(MediaHistoryTable.Instance)
                .Limit(1);
            command.PutQueryInt64(MediaHistoryTable.ColumnMediaId);
            using SelectCommand.IReader reader = command.Execute();
            return !reader.Read();
        });
    }

    //
    // Setters
    //

    public static async Task AddAsync(long id, string title, bool suppressEvent = false)
    {
        await Enqueue(() =>
        {
            DeleteCommand deleteCommand = DeleteCommand.Create(MediaHistoryTable.Instance)
                .AppendCondition(MediaHistoryTable.ColumnMediaId, id);
            deleteCommand.Execute();

            InsertCommand insertCommand = InsertCommand.Create(MediaHistoryTable.Instance)
                .AppendColumn(MediaHistoryTable.ColumnMediaId, id)
                .AppendColumn(MediaHistoryTable.ColumnTitle, title)
                .AppendColumn(MediaHistoryTable.ColumnTime, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            insertCommand.Execute();

            return true;
        });

        if (!suppressEvent)
        {
            DispatchUpdateEvent();
        }
    }

    public static async Task RemoveAsync(long id, bool suppressEvent = false)
    {
        bool changed = await Enqueue(() =>
        {
            DeleteCommand deleteCommand = DeleteCommand.Create(MediaHistoryTable.Instance)
                .AppendCondition(MediaHistoryTable.ColumnMediaId, id);
            int rowsAffected = deleteCommand.Execute();
            return rowsAffected > 0;
        });

        if (changed && !suppressEvent)
        {
            DispatchUpdateEvent();
        }
    }

    public static async Task ClearAsync(bool suppressEvent = false)
    {
        await Enqueue(() =>
        {
            var deleteCommand = DeleteCommand.Create(MediaHistoryTable.Instance);
            deleteCommand.Execute();
            return true;
        });

        if (!suppressEvent)
        {
            DispatchUpdateEvent();
        }
    }

    private static void DispatchUpdateEvent()
    {
        GlobalEvent.Instance.HistoryUpdated.Emit(0);
    }

    private static Task<T> Enqueue<T>(Func<T> op)
    {
        return SqliteDB.MiscDatabaseDispatcher.Submit(op);
    }
}
