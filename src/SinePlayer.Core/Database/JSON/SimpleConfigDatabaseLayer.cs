// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Database.Misc;

namespace SinePlayer.Core.Database.JSON;

public class SimpleConfigDatabaseLayer(string filename) : IConfigBackingLayer
{
    private readonly string _filename = filename;
    private readonly ITaskDispatcher _queue = TaskDispatcher.Factory.NewQueue($"{nameof(JsonDatabase<>)}#{filename}");

    public string? ReadConfig()
    {
        return SimpleConfigDatabase.Instance.TryGetConfig(_filename);
    }

    public void WriteConfig(string value)
    {
        _queue.Submit(() =>
        {
            SimpleConfigDatabase.Instance.TryPutConfig(_filename, value);
        });
    }
}
