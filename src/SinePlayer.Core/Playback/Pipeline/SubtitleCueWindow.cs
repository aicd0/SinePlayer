// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class SubtitleCueWindow
{
    private readonly Lock _lock = new();
    private readonly List<RefCounted<SubtitleCue>> _cues = [];

    /// <summary>
    /// The furthest position anything has asked for since the last jump: the floor a cue has to clear to be worth keeping.
    /// The reader runs ahead of the renderer, but the renderer only ever asks about later positions, so a cue ending
    /// behind the last question can never be on screen again — no margin is needed, and none is guessed.
    /// </summary>
    private TimeSpan _asked = TimeSpan.MinValue;

    public void Reset()
    {
        lock (_lock)
        {
            foreach (RefCounted<SubtitleCue> hold in _cues)
            {
                hold.Unref();
            }

            _cues.Clear();
            _asked = TimeSpan.MinValue;
        }
    }

    /// <summary>
    /// Whether anything draws these cues, which decides how far behind a cue may fall before it is dropped. It is an
    /// argument rather than something the window remembers because <see cref="Add"/> is the only place it matters, and the
    /// caller knows it exactly then — a remembered flag would only be as fresh as the last event.
    /// </summary>
    public void Add(IReadOnlyList<SubtitleCue> cues, TimeSpan position, bool hasConsumer = true)
    {
        lock (_lock)
        {
            TimeSpan floor = hasConsumer ? _asked : position;
            foreach (SubtitleCue cue in cues)
            {
                CloseOpenEndedBefore(cue.Start);

                // The window's own reference to the cue it was just handed. Letting go of it frees the picture only once
                // no snapshot holds one too, which is what makes dropping a cue safe while it is still being drawn.
                RefCounted<SubtitleCue> hold = new(cue);
                if (cue.End > floor && !Holds(cue))
                {
                    _cues.Add(hold);
                }
                else
                {
                    hold.Unref();
                }
            }

            TrimTo(floor);
        }
    }

    /// <summary>
    /// Ends every cue still waiting to be told when it ends at <paramref name="position"/>, which is what a jump means:
    /// the track is no longer being read in order, so nothing that arrives later says how long what was on screen would
    /// have stayed. A cue the playhead is about to be inside is read again from the file and comes back with its own
    /// start; one it has jumped past does not, and that is the point.
    /// </summary>
    public void SkipTo(TimeSpan position)
    {
        lock (_lock)
        {
            CloseOpenEndedBefore(position);

            // The renderer jumps with the playhead, so it will not ask about anything before the landing: the floor moves
            // with it rather than staying where it was, which is what makes a backward jump recover what it lands on.
            _asked = position;
            TrimTo(position);
        }
    }

    /// <summary>
    /// The cues that belong on screen at <paramref name="position"/>, in the order they were decoded, as a hold the caller
    /// releases when it is done with them — a renderer may still be drawing them after the playhead has moved on.
    /// </summary>
    public SubtitleCueSnapshot Visible(TimeSpan position)
    {
        lock (_lock)
        {
            if (position > _asked)
            {
                _asked = position;
            }

            // Asking is what drains: everything behind the question has been passed for good, and this is the moment the
            // window knows it.
            TrimTo(_asked);

            List<RefCounted<SubtitleCue>>? visible = null;
            foreach (RefCounted<SubtitleCue> hold in _cues)
            {
                SubtitleCue cue = hold.Value;
                if (cue.Start <= position && position < cue.End)
                {
                    // The window's own reference is still here, so this cannot be a cue that has already been freed.
                    hold.Ref();
                    (visible ??= []).Add(hold);
                }
            }

            return visible is null ? SubtitleCueSnapshot.Empty : SubtitleCueSnapshot.Lease(visible);
        }
    }

    /// <summary>
    /// How long until what is on screen, or about to be, can change — the next cue ending or the next cue starting,
    /// whichever comes first. Null when nothing is waiting, which is what a track silent for the rest of the file looks
    /// like.
    /// </summary>
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
                if (next is null || wait < next.Value)
                {
                    next = wait;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// Lets go of every cue that ends at or before <paramref name="floor"/>, which no future question can include. A
    /// snapshot holding one keeps the picture alive past this point; only the window's own reference is given up.
    /// </summary>
    private void TrimTo(TimeSpan floor)
    {
        int stale = 0;
        while (stale < _cues.Count && _cues[stale].Value.End <= floor)
        {
            stale++;
        }

        for (int index = 0; index < stale; index++)
        {
            _cues[index].Unref();
        }

        if (stale > 0)
        {
            _cues.RemoveRange(0, stale);
        }
    }

    /// <summary>
    /// Ends every cue still waiting to be told when it ends at <paramref name="start"/>, the beginning of the event that
    /// takes its place. A cue that already stated its end is left at that end: it may be a text line whose successor is
    /// written on top of it. Called under the lock, which is what makes writing an open cue's end safe — everything that
    /// reads a cue's end reads it here too.
    /// </summary>
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

    /// <summary>
    /// Whether this is the same event as one already held. Two events may share their times — that is how simultaneous
    /// dialogue is written — so the text has to match as well; the same text from the same moment is the same event read
    /// twice, which is what a re-read after a seek produces. The end is not compared because an open cue's end is
    /// decided by what came after it, so a re-read would never match the copy that has since been closed. Called under
    /// the lock.
    /// </summary>
    private bool Holds(SubtitleCue cue)
    {
        foreach (RefCounted<SubtitleCue> hold in _cues)
        {
            SubtitleCue held = hold.Value;
            if (held.Start == cue.Start && string.Equals(held.Text, cue.Text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
