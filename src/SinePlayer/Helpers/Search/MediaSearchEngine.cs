// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using SinePlayer.Common.Expression;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Database.SqlHelpers;
using SinePlayer.Data.Models.Media;
using SinePlayer.Data.Tables;

namespace SinePlayer.Helpers.Search;

internal class MediaSearchEngine
{
    private const string TAG = nameof(MediaSearchEngine);

    private readonly ITaskDispatcher _dispatcher = TaskDispatcher.DefaultQueue;
    private int _updateSubmitted = 0;

    private Action<IReadOnlyList<MediaModel>>? _resultCallback = null;
    private readonly List<MediaModel> _mediaItems = [];

    public string SearchText { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public bool IncludeHidden { get; set; } = false;
    public bool ExcludeInCollectionMedia { get; set; } = false;
    public ICondition? CustomCondition { get; set; } = null;

    public void SetResultCallback(Action<IReadOnlyList<MediaModel>>? callback)
    {
        _resultCallback = callback;
    }

    public void Update()
    {
        ScheduleUpdate();
    }

    private void ScheduleUpdate()
    {
        if (Interlocked.CompareExchange(ref _updateSubmitted, 1, 0) == 1)
        {
            return;
        }

        _dispatcher.SubmitAsync(async () =>
        {
            Interlocked.Exchange(ref _updateSubmitted, 0);
            await UpdateNoLock();
        });
    }

    private async Task UpdateNoLock()
    {
        Logger.I(TAG, "UpdateNoLock");

        List<long> ids = await SearchByKeywords();
        List<MediaModel> mediaItems = await MediaModel.BatchFromId(ids);

        Dictionary<long, int> order = [];
        for (int i = 0; i < ids.Count; i++)
        {
            order[ids[i]] = i;
        }

        mediaItems.Sort((x, y) =>
        {
            if (!order.TryGetValue(x.Id, out int xIndex))
            {
                xIndex = int.MaxValue;
            }

            if (!order.TryGetValue(y.Id, out int yIndex))
            {
                yIndex = int.MaxValue;
            }

            return xIndex - yIndex;
        });

        _mediaItems.Clear();
        _mediaItems.AddRange(mediaItems);
        _resultCallback?.Invoke(mediaItems);
    }

    private ICondition? ParseExpression(string expression)
    {
        Common.Expression.Filter.ExpressionToken token;
        try
        {
            token = ExpressionParser.ParseFilter(expression);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            return null;
        }

        ICondition condition;
        try
        {
            condition = Common.Expression.Filter.Sql.SQLGenerator.CreateQuery(token, new MediaFilterSQLProvider());
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            return null;
        }

        return condition;
    }

    private ICondition? ParseSearchExpresssion(string expression, bool includeHidden, out List<string> remainingKeywords)
    {
        List<Common.Expression.Search.ExpressionToken> tokens;
        try
        {
            tokens = ExpressionParser.ParseSearch(expression);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            remainingKeywords = [.. expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return null;
        }

        ICondition condition;
        try
        {
            condition = Common.Expression.Search.Sql.SQLGenerator.CreateQuery(tokens, includeHidden, new MediaSearchSQLProvider(new MediaFilterSQLProvider()), out remainingKeywords);
        }
        catch (Exception ex)
        {
            Logger.E(TAG, ex);
            remainingKeywords = [.. expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return null;
        }

        return condition;
    }

    private async Task<List<long>> SearchByKeywords()
    {
        string searchText = SearchText;
        bool includeHidden = IncludeHidden;
        bool excludeInCollectionMedia = ExcludeInCollectionMedia;
        ICondition? expressionCondition = ParseExpression(Expression);
        ICondition? customCondition = CustomCondition;

        ICondition? searchCondition = ParseSearchExpresssion(searchText, includeHidden, out List<string> remaining);
        for (int i = 0; i < remaining.Count; i++)
        {
            remaining[i] = remaining[i].ToLowerInvariant();
        }

        var matches = new List<Match>();
        await MediaHandle.Enqueue(() =>
        {
            var command = SelectCommand.Create(MediaTable.Instance);
            IReaderToken<long> idToken = command.PutQueryInt64(MediaTable.ColumnId);
            IReaderToken<string> title1Token = command.PutQueryString(MediaTable.ColumnTitle1);
            IReaderToken<string> title2Token = command.PutQueryString(MediaTable.ColumnTitle2);

            if (searchCondition is not null)
            {
                command.AppendCondition(searchCondition);
            }

            if (expressionCondition is not null)
            {
                command.AppendCondition(expressionCondition);
            }

            if (customCondition is not null)
            {
                command.AppendCondition(customCondition);
            }

            if (excludeInCollectionMedia)
            {
                command.AppendCondition(CreateExcludeInCollectionsCondition());
            }

            using SelectCommand.IReader reader = command.Execute();
            while (reader.Read())
            {
                int similarity = 0;
                string title1 = title1Token.GetValue();
                string title2 = title2Token.GetValue();

                if (remaining.Count > 0)
                {
                    string matchText = (title1 + " " + title2).ToLowerInvariant();
                    similarity = StringUtils.FastMatch(remaining, matchText);
                    if (similarity < 1)
                    {
                        continue;
                    }
                }

                matches.Add(new Match
                {
                    Id = idToken.GetValue(),
                    Similarity = similarity,
                    SortTitle = title1 + " " + title2
                });
            }

            var all = new List<long>(matches.Count);
            foreach (Match match in matches)
            {
                all.Add(match.Id);
            }

            return true;
        });

        matches.Sort((x, y) =>
        {
            return y.Similarity - x.Similarity;
        });

        List<long> ids = new(matches.Count);
        foreach (Match match in matches)
        {
            ids.Add(match.Id);
        }

        return ids;
    }

    private static ICondition CreateExcludeInCollectionsCondition()
    {
        var subquery = SelectCommand.Create(MediaCollectionTable.Instance);
        _ = subquery.PutQueryInt64(MediaCollectionTable.ColumnMediaId);
        return new NotCondition(new InCondition(ColumnOrValue.FromColumn(MediaTable.ColumnId), subquery));
    }

    private class Match
    {
        public long Id;
        public int Similarity = 0;
        public string SortTitle = "";
    }
}
