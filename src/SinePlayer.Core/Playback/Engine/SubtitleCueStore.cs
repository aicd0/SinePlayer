// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class SubtitleCueStore
{
    /// <summary>A cue, the hold around it and the entry that files it, which is what every event costs besides its content.</summary>
    private const long CueOverhead = 160;

    /// <summary>A string's header, on top of the characters themselves.</summary>
    private const long StringOverhead = 24;

    /// <summary>One <see cref="SubtitleChange"/>: two times and a flag, rounded to a machine word.</summary>
    private const long ChangeOverhead = 32;

    private readonly List<Entry> _entries = [];
    private readonly List<Entry> _openEnded = [];
    private readonly LinkedList<Entry> _recent = [];
    private readonly HashSet<Entry> _doomed = [];
    private long _size;
    private TimeSpan _longestSpan;

    /// <summary>The entries, ordered by where they begin.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>What the entries cost, in bytes, as far as the pictures and words they own go.</summary>
    public long Size => _size;

    /// <summary>
    /// The longest stretch a settled cue covers. A cue that begins earlier than this before the playhead cannot still be
    /// up, which is what lets a question about a position stop walking backwards instead of reading the whole track.
    /// Never decreased: a value that is too large only costs a longer walk, while one that is too small would lose a cue.
    /// </summary>
    public TimeSpan LongestSpan => _longestSpan;

    /// <summary>
    /// The event already held at that start with those words, live or passed. Matching on both is what tells two
    /// speakers at once — the same span, different words — apart from one event read twice.
    /// </summary>
    public Entry? Find(TimeSpan start, string? text)
    {
        for (int index = LowerBound(start); index < _entries.Count; index++)
        {
            SubtitleCue cue = _entries[index].Hold.Value;
            if (cue.Start != start)
            {
                return null;
            }

            if (cue.Text == text)
            {
                return _entries[index];
            }
        }

        return null;
    }

    /// <summary>Takes ownership of the cue and files it where its start belongs.</summary>
    public Entry Insert(SubtitleCue cue)
    {
        Entry entry = new(new RefCounted<SubtitleCue>(cue), CostOf(cue));
        entry.Node = _recent.AddFirst(entry);

        // Events arrive in the order the file states them, so the new one almost always belongs at the end.
        if (_entries.Count == 0 || _entries[^1].Hold.Value.Start <= cue.Start)
        {
            _entries.Add(entry);
        }
        else
        {
            _entries.Insert(LowerBound(cue.Start), entry);
        }

        if (cue.IsOpenEnded)
        {
            _openEnded.Add(entry);
        }
        else if (cue.End - cue.Start > _longestSpan)
        {
            _longestSpan = cue.End - cue.Start;
        }

        _size += entry.Size;
        return entry;
    }

    /// <summary>
    /// Ends every open cue that began before this moment, which is what an event beginning there says about the ones
    /// before it when the track itself stated nothing.
    /// </summary>
    public void CloseOpenBefore(TimeSpan start)
    {
        for (int index = _openEnded.Count - 1; index >= 0; index--)
        {
            Entry entry = _openEnded[index];
            SubtitleCue cue = entry.Hold.Value;
            if (cue.Start >= start)
            {
                continue;
            }

            cue.End = start;
            _openEnded.RemoveAt(index);

            if (cue.End - cue.Start > _longestSpan)
            {
                _longestSpan = cue.End - cue.Start;
            }
        }
    }

    /// <summary>
    /// Drops every open cue that began before this moment without ending it: how long it was up is a fact about events
    /// that lie between where it began and where the playhead now is, and a jump never reads them.
    /// </summary>
    public void DiscardOpenBefore(TimeSpan position)
    {
        for (int index = _openEnded.Count - 1; index >= 0; index--)
        {
            Entry entry = _openEnded[index];
            if (entry.Hold.Value.Start < position)
            {
                Remove(entry);
            }
        }
    }

    /// <summary>Records that the entry was just asked for, which is the order the budget takes entries away in.</summary>
    public void Touch(Entry entry)
    {
        if (entry.Node is not { } node || ReferenceEquals(_recent.First, node))
        {
            return;
        }

        _recent.Remove(node);
        _recent.AddFirst(node);
    }

    /// <summary>How many entries begin at or before this moment, which bounds where anything that has ended can lie.</summary>
    public int CountAtOrBefore(TimeSpan position)
    {
        return UpperBound(position);
    }

    /// <summary>Whether a cue the track never ended began at or before this moment.</summary>
    public bool HasOpenBefore(TimeSpan position)
    {
        foreach (Entry entry in _openEnded)
        {
            if (entry.Hold.Value.Start <= position)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the least recently asked for entries whose end is at or before the floor away until the store costs no more
    /// than the given limit, or returns without doing anything when there is nothing that may go.
    /// <para>
    /// Walking back from the oldest stops as soon as enough has been freed, and the whole batch is taken out in one pass,
    /// so the work is the length of the history plus the number of victims, not that times the number of victims.
    /// </para>
    /// </summary>
    public void Trim(TimeSpan floor, long limit)
    {
        if (_size <= limit)
        {
            return;
        }

        long size = _size;
        for (LinkedListNode<Entry>? node = _recent.Last; node is not null && size > limit; node = node.Previous)
        {
            Entry entry = node.Value;
            if (entry.Hold.Value.End > floor)
            {
                continue;
            }

            _doomed.Add(entry);
            size -= entry.Size;
        }

        if (_doomed.Count == 0)
        {
            return;
        }

        RemoveAll(_doomed);
        _doomed.Clear();
    }

    public void RemoveAt(int index)
    {
        Entry entry = _entries[index];
        _entries.RemoveAt(index);
        Detach(entry);
    }

    public void Remove(Entry entry)
    {
        int index = _entries.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        _entries.RemoveAt(index);
        Detach(entry);
    }

    /// <summary>
    /// Removes a set of entries in one pass. Taking a whole batch away is the ordinary case when the budget is exceeded,
    /// and doing it one removal at a time would shift the rest of the list once per victim.
    /// </summary>
    public void RemoveAll(ISet<Entry> doomed)
    {
        if (doomed.Count == 0)
        {
            return;
        }

        int write = 0;
        int count = _entries.Count;
        for (int read = 0; read < count; read++)
        {
            Entry entry = _entries[read];
            if (doomed.Contains(entry))
            {
                Detach(entry);
                continue;
            }

            _entries[write++] = entry;
        }

        _entries.RemoveRange(write, count - write);
    }

    public void Clear()
    {
        foreach (Entry entry in _entries)
        {
            entry.Hold.Unref();
        }

        _entries.Clear();
        _openEnded.Clear();
        _recent.Clear();
        _size = 0;
        _longestSpan = TimeSpan.Zero;
    }

    /// <summary>Forgets one entry's bookkeeping and lets go of what it held.</summary>
    private void Detach(Entry entry)
    {
        _openEnded.Remove(entry);
        if (entry.Node is { } node)
        {
            _recent.Remove(node);
            entry.Node = null;
        }

        _size -= entry.Size;
        entry.Hold.Unref();
    }

    /// <summary>The first entry that begins at or after this moment.</summary>
    private int LowerBound(TimeSpan start)
    {
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].Hold.Value.Start < start)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The first entry that begins after this moment.</summary>
    private int UpperBound(TimeSpan position)
    {
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].Hold.Value.Start <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// What an entry costs. A cue that owns a picture is counted by those pixels, because that is the memory a budget
    /// has to hold back; text is counted by the words it carries and the animation instants it names, which is small per
    /// event but is the only thing that bounds a track of tens of thousands of them.
    /// </summary>
    private static long CostOf(SubtitleCue cue)
    {
        if (cue.Image is { } image)
        {
            return CueOverhead + image.Buffer.Length;
        }

        long cost = CueOverhead + StringOverhead + ((cue.Text?.Length ?? 0) * sizeof(char));
        if (cue.Changes is { Count: > 0 } changes)
        {
            cost += changes.Count * ChangeOverhead;
        }

        return cost;
    }

    internal sealed class Entry(RefCounted<SubtitleCue> hold, long size)
    {
        public RefCounted<SubtitleCue> Hold { get; } = hold;

        public long Size { get; } = size;

        /// <summary>Where the entry sits in the order it was last asked for in, or null once it has been taken out.</summary>
        public LinkedListNode<Entry>? Node { get; set; }
    }
}
