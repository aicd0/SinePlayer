// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class SubtitleCueWindow
{
    private const long DefaultCapacity = 32L * 1024 * 1024;
    private const long MinimumCapacity = 8L * 1024 * 1024;
    private const long MaximumCapacity = 4096L * 1024 * 1024;
    private const long UnknownEpoch = long.MinValue;

    private readonly Lock _lock = new();
    private readonly SubtitleCueStore _store = new();
    private long _epoch = UnknownEpoch;
    private TimeSpan _floor = TimeSpan.MinValue;
    private TimeSpan _trimmedFloor = TimeSpan.MinValue;
    private long _capacity = DefaultCapacity;

    /// <summary>
    /// How much the kept cues may cost, in bytes. Setting it takes effect at once: what is over the new budget and may
    /// go is let go of, so turning the setting down frees the memory it was meant to free rather than at the next cue.
    /// </summary>
    public long Capacity
    {
        get
        {
            lock (_lock)
            {
                return _capacity;
            }
        }
        set
        {
            long capacity = Math.Clamp(value, MinimumCapacity, MaximumCapacity);
            lock (_lock)
            {
                if (_capacity == capacity)
                {
                    return;
                }

                _capacity = capacity;
                _trimmedFloor = TimeSpan.MinValue;
                Trim();
            }
        }
    }

    /// <summary>
    /// How much of the budget the trimming leaves free. Taking away exactly what was over the line would trim on every
    /// cue once the track reached the budget, and pay for the history walk each time; leaving a margin makes that cost
    /// one walk per margin's worth of new cues instead, and the footprint stays under the budget either way.
    /// </summary>
    private long Slack => _capacity / 16;

    /// <summary>What the kept cues cost, which is what the budget limits.</summary>
    internal long ApproximateSize
    {
        get
        {
            lock (_lock)
            {
                return _store.Size;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _store.Clear();
            _floor = TimeSpan.MinValue;
            _trimmedFloor = TimeSpan.MinValue;
        }
    }

    /// <param name="readerIsTheFloor">
    /// Set when nothing is drawing this window, so nothing will ever ask: the position the reader has reached is then
    /// the only thing that can say what has been passed. It says nothing about what is worth keeping — cold cues stay
    /// while the budget allows, exactly as they do when a renderer is asking.
    /// </param>
    public void Add(long epoch, IReadOnlyList<SubtitleCue> cues, TimeSpan position, bool readerIsTheFloor = false)
    {
        lock (_lock)
        {
            if (epoch == UnknownEpoch)
            {
                _epoch = epoch;
            }
            else if (epoch != _epoch)
            {
                foreach (SubtitleCue cue in cues)
                {
                    cue.Dispose();
                }

                return;
            }

            if (readerIsTheFloor && position > _floor)
            {
                _floor = position;
            }

            foreach (SubtitleCue cue in cues)
            {
                _store.CloseOpenBefore(cue.Start);

                if (_store.Find(cue.Start, cue.Text) is { } held)
                {
                    _store.Touch(held);
                    cue.Dispose();
                    continue;
                }

                _store.Insert(cue);
            }

            Trim();
        }
    }

    public void SeekTo(long epoch, TimeSpan position)
    {
        lock (_lock)
        {
            _epoch = epoch;
            _floor = position;
            _store.DiscardOpenBefore(position);
            Trim();
        }
    }

    public SubtitleCueSnapshot GetVisibleCues(long epoch, TimeSpan position)
    {
        lock (_lock)
        {
            if (epoch != _epoch)
            {
                return SubtitleCueSnapshot.Empty;
            }

            if (position > _floor)
            {
                _floor = position;
            }

            IReadOnlyList<SubtitleCueStore.Entry> entries = _store.Entries;
            TimeSpan horizon = position - _store.LongestSpan;
            bool openBefore = _store.HasOpenBefore(position);

            List<RefCounted<SubtitleCue>>? visible = null;
            for (int index = _store.CountAtOrBefore(position) - 1; index >= 0; index--)
            {
                SubtitleCueStore.Entry entry = entries[index];
                SubtitleCue cue = entry.Hold.Value;

                // Everything from here back begins too early to still be up, unless the track left one of them open.
                if (!openBefore && cue.Start < horizon)
                {
                    break;
                }

                if (cue.Start <= position && position < cue.End)
                {
                    _store.Touch(entry);
                    entry.Hold.Ref();
                    (visible ??= []).Add(entry.Hold);
                }
            }

            if (visible is null)
            {
                return SubtitleCueSnapshot.Empty;
            }

            // The walk collected them from the newest back, and a renderer draws them in the order the track stated.
            visible.Reverse();
            return SubtitleCueSnapshot.Lease(visible);
        }
    }

    public TimeSpan? TimeUntilNextChange(TimeSpan position)
    {
        lock (_lock)
        {
            IReadOnlyList<SubtitleCueStore.Entry> entries = _store.Entries;
            TimeSpan horizon = position - _store.LongestSpan;
            bool openBefore = _store.HasOpenBefore(position);
            int count = _store.CountAtOrBefore(position);

            TimeSpan? next = null;
            for (int index = count - 1; index >= 0; index--)
            {
                SubtitleCue cue = entries[index].Hold.Value;
                if (!openBefore && cue.Start < horizon)
                {
                    break;
                }

                if (cue.End <= position)
                {
                    continue;
                }

                TimeSpan boundary = cue.Start > position ? cue.Start : cue.End;
                TimeSpan wait = boundary - position;

                if (cue.TimeUntilChange(position) is { } change && change < wait)
                {
                    wait = change;
                }

                if (next is null || wait < next.Value)
                {
                    next = wait;
                }
            }

            // Anything further ahead can only be later than the next event to begin.
            if (count < entries.Count)
            {
                TimeSpan wait = entries[count].Hold.Value.Start - position;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                if (next is null || wait < next.Value)
                {
                    next = wait;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// Takes cold entries away until the budget fits. Only what the playhead has passed may go, and never what is still
    /// up: a budget is met by forgetting the past, not by taking away the answer to the question being asked. The ones
    /// that have waited longest go first, which keeps a place being watched from being forgotten under the viewer.
    /// <para>
    /// It runs where cues are put in and where a jump is taken — the reading side — and not where they are asked for, so
    /// a backlog is cleared without ever holding up the renderer. A pass that finds nothing is not repeated for the same
    /// position, because a cue that arrives is one the playhead has not reached, and so cannot be one that may go.
    /// </para>
    /// </summary>
    private void Trim()
    {
        if (_floor <= _trimmedFloor || _store.Size <= _capacity)
        {
            return;
        }

        _trimmedFloor = _floor;
        _store.Trim(_floor, _capacity - Slack);
    }
}
