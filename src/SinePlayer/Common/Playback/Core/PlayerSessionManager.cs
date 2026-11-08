// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Threading;

namespace SinePlayer.Common.Playback.Core;

internal sealed class PlayerSessionManager
{
    public static PlayerSessionManager Shared { get; } = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public PlayerSession Acquire(string id, out bool created)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out Entry? entry))
            {
                entry.Pages++;
                created = false;
                return entry.Session;
            }

            PlayerSession session = new(id);
            _sessions[id] = new Entry(session);
            created = true;
            return session;
        }
    }

    public void Release(string id)
    {
        PlayerSession? ended;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out Entry? entry))
            {
                return;
            }

            entry.Pages--;
            if (entry.Pages > 0)
            {
                return;
            }

            _sessions.Remove(id);
            ended = entry.Session;
        }

        ended.Dispose();
    }

    private sealed class Entry(PlayerSession session)
    {
        public PlayerSession Session { get; } = session;

        public int Pages { get; set; } = 1;
    }
}
