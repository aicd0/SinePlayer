// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class SubtitleCueWindow
{
    private const long UnknownEpoch = long.MinValue;

    private readonly Lock _lock = new();
    private readonly List<RefCounted<SubtitleCue>> _cues = [];
    private long _epoch = UnknownEpoch;
    private TimeSpan _seekPosition = TimeSpan.MinValue;
    private TimeSpan _consumedPosition = TimeSpan.MinValue;

    public void Reset()
    {
        lock (_lock)
        {
            foreach (RefCounted<SubtitleCue> hold in _cues)
            {
                hold.Unref();
            }

            _cues.Clear();
            _seekPosition = TimeSpan.MinValue;
            _consumedPosition = TimeSpan.MinValue;
        }
    }

    public void Add(long epoch, IReadOnlyList<SubtitleCue> cues, TimeSpan position, bool discardStale = false)
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

            TimeSpan floor = discardStale ? position : _seekPosition;
            foreach (SubtitleCue cue in cues)
            {
                CloseOpenEndedBefore(cue.Start);

                RefCounted<SubtitleCue> hold = new(cue);
                if (cue.End > floor && !HoldCue(cue))
                {
                    Insert(hold);
                }
                else
                {
                    hold.Unref();
                }
            }

            DiscardStaleCues(floor);
        }
    }

    public void SeekTo(long epoch, TimeSpan position)
    {
        lock (_lock)
        {
            _epoch = epoch;
            _seekPosition = position;
            _consumedPosition = position;
            CloseOpenEndedBefore(position);
            DiscardStaleCues(position);
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

            if (position > _consumedPosition)
            {
                _consumedPosition = position;
            }

            DiscardStaleCues(_consumedPosition);

            List<RefCounted<SubtitleCue>>? visible = null;
            foreach (RefCounted<SubtitleCue> hold in _cues)
            {
                SubtitleCue cue = hold.Value;
                if (cue.Start <= position && position < cue.End)
                {
                    hold.Ref();
                    (visible ??= []).Add(hold);
                }
            }

            return visible is null ? SubtitleCueSnapshot.Empty : SubtitleCueSnapshot.Lease(visible);
        }
    }

    public TimeSpan? TimeUntilNextChange(TimeSpan position)
    {
        lock (_lock)
        {
            TimeSpan? next = null;
            foreach (RefCounted<SubtitleCue> hold in _cues)
            {
                SubtitleCue cue = hold.Value;
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

            return next;
        }
    }

    private bool HoldCue(SubtitleCue cue)
    {
        foreach (RefCounted<SubtitleCue> hold in _cues)
        {
            SubtitleCue held = hold.Value;
            if (held.Start == cue.Start && held.Text == cue.Text)
            {
                return true;
            }
        }

        return false;
    }

    private void Insert(RefCounted<SubtitleCue> hold)
    {
        int index = _cues.Count;
        while (index > 0 && _cues[index - 1].Value.Start > hold.Value.Start)
        {
            index--;
        }

        _cues.Insert(index, hold);
    }

    private void CloseOpenEndedBefore(TimeSpan start)
    {
        foreach (RefCounted<SubtitleCue> hold in _cues)
        {
            SubtitleCue held = hold.Value;
            if (held.IsOpenEnded && held.Start < start)
            {
                held.End = start;
            }
        }
    }

    private void DiscardStaleCues(TimeSpan floor)
    {
        for (int index = _cues.Count - 1; index >= 0; index--)
        {
            RefCounted<SubtitleCue> hold = _cues[index];
            if (hold.Value.End > floor)
            {
                continue;
            }

            _cues.RemoveAt(index);
            hold.Unref();
        }
    }
}
