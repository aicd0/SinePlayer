// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The cues of one subtitle track that can still be on screen, in the order they were decoded. The reader adds to it as
/// it decodes, and whoever draws subtitles reads it to learn what belongs on screen at a position.
/// <para>
/// Nothing here belongs to a position or a generation. A cue is a timestamped fact about the file, so a cue decoded
/// before a seek is as true after it as before, and "what is on screen" is the same question whether the playhead got
/// here by playing or by seeking. What a seek does change is how the same fact arrives twice — the reader re-reads from
/// a keyframe, so cues ahead of the new position are decoded a second time — which is why <see cref="Add"/> recognises
/// a cue it already holds instead of counting it again.
/// </para>
/// <para>
/// It owns what it holds: a cue is disposed once it falls behind the playhead, so a consumer sees only cues that are
/// still worth drawing.
/// </para>
/// </summary>
internal sealed class SubtitleCueWindow
{
    /// <summary>
    /// How long a cue is kept past its end. A consumer may be drawing a cue at this very moment, holding a reference
    /// the window gave it, so nothing may be freed the instant it stops being visible.
    /// </summary>
    private static readonly TimeSpan TrimMargin = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();

    /// <summary>Appended in the order the packets arrive, which is the order of their start times.</summary>
    private readonly List<SubtitleCue> _cues = [];

    /// <summary>Drops everything, for when the track or the file goes away.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (SubtitleCue cue in _cues)
            {
                cue.Dispose();
            }

            _cues.Clear();
        }
    }

    /// <summary>
    /// Takes ownership of the cues, skipping any it already holds, and drops those already behind
    /// <paramref name="position"/> — including the ones that were waiting here and have been overtaken while the
    /// playhead advanced.
    /// </summary>
    public void Add(IReadOnlyList<SubtitleCue> cues, TimeSpan position)
    {
        lock (_lock)
        {
            TimeSpan oldest = position - TrimMargin;
            foreach (SubtitleCue cue in cues)
            {
                if (cue.End > oldest && !Holds(cue))
                {
                    _cues.Add(cue);
                }
                else
                {
                    cue.Dispose();
                }
            }

            int stale = 0;
            while (stale < _cues.Count && _cues[stale].End <= oldest)
            {
                stale++;
            }

            for (int index = 0; index < stale; index++)
            {
                _cues[index].Dispose();
            }

            if (stale > 0)
            {
                _cues.RemoveRange(0, stale);
            }
        }
    }

    /// <summary>
    /// The cues that belong on screen at <paramref name="position"/>, in the order they were decoded. The cues stay the
    /// window's: they are visible, so they are not dropped under the caller's feet.
    /// </summary>
    public IReadOnlyList<SubtitleCue> Visible(TimeSpan position)
    {
        lock (_lock)
        {
            if (_cues.Count == 0)
            {
                return [];
            }

            List<SubtitleCue> visible = [];
            foreach (SubtitleCue cue in _cues)
            {
                if (cue.Start <= position && position < cue.End)
                {
                    visible.Add(cue);
                }
            }

            return visible;
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
            foreach (SubtitleCue cue in _cues)
            {
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
    /// Whether this is the same event as one already held. Two events may share their times — that is how simultaneous
    /// dialogue is written — so the text has to match as well; the same text over the same span is the same event read
    /// twice, which is what a re-read after a seek produces. Called under the lock.
    /// </summary>
    private bool Holds(SubtitleCue cue)
    {
        foreach (SubtitleCue held in _cues)
        {
            if (held.Start == cue.Start && held.End == cue.End && string.Equals(held.Text, cue.Text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
