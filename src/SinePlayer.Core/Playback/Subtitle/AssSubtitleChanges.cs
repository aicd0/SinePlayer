// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Subtitle;

/// <summary>
/// Reads an ASS dialogue line and says when its picture changes by itself: the <c>\t</c>, <c>\move</c> and fade windows
/// it declares, and the instants its karaoke syllables turn over.
/// <para>
/// This is scheduling information, not drawing: a backend that animates still has to interpret the tags. What the
/// pipeline needs from here is only whether waiting for the next cue boundary would leave an animation frozen, and the
/// answer has to be cheap enough to compute once per cue — a cue with no tag in this list is drawn once and never
/// again, which is most of them.
/// </para>
/// </summary>
internal static class AssSubtitleChanges
{
    /// <summary>
    /// The windows during which <paramref name="eventLine"/> changes, in absolute time, or null when it never does.
    /// </summary>
    /// <param name="eventLine">
    /// The line as the decoder hands it over — <c>Layer,Style,Speaker,MarginL,MarginR,MarginV,Effect,Text</c> — or the
    /// bare text, which is what a line that was already cut has.
    /// </param>
    /// <param name="start">When the cue begins, which is what makes the tags' times absolute.</param>
    /// <param name="end">
    /// When the cue ends, or <see cref="SubtitleCue.OpenEnd"/> when that is not known yet. A fade's tail depends on it,
    /// so an open cue is treated as fading for its whole life instead of guessing.
    /// </param>
    internal static SubtitleChange[]? Parse(string? eventLine, TimeSpan start, TimeSpan end)
    {
        if (string.IsNullOrWhiteSpace(eventLine))
        {
            return null;
        }

        // Eight fields, and the eighth is the whole rest of the line: dialogue is allowed to contain commas.
        string[] fields = eventLine.Split(',', 8);
        string text = fields.Length == 8 ? fields[7] : eventLine;

        List<SubtitleChange>? changes = null;
        List<TimeSpan>? karaoke = null;
        TimeSpan karaokeOffset = TimeSpan.Zero;

        int index = 0;
        while (index < text.Length)
        {
            if (text[index] != '{')
            {
                index++;
                continue;
            }

            int close = text.IndexOf('}', index + 1);
            if (close < 0)
            {
                break;
            }

            ReadBlock(text.AsSpan(index + 1, close - index - 1), start, end, ref changes, ref karaoke, ref karaokeOffset);
            index = close + 1;
        }

        if (karaoke is not null)
        {
            foreach (TimeSpan moment in karaoke)
            {
                changes ??= [];
                changes.Add(new SubtitleChange(moment, moment, false));
            }
        }

        return changes?.ToArray();
    }

    private static void ReadBlock(
        ReadOnlySpan<char> block,
        TimeSpan start,
        TimeSpan end,
        ref List<SubtitleChange>? changes,
        ref List<TimeSpan>? karaoke,
        ref TimeSpan karaokeOffset)
    {
        int index = 0;
        while (index < block.Length)
        {
            if (block[index] != '\\')
            {
                index++;
                continue;
            }

            index++;
            int nameStart = index;
            while (index < block.Length && char.IsLetter(block[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                continue;
            }

            ReadOnlySpan<char> name = block[nameStart..index];

            // A parenthesised argument is taken whole, parentheses and all: it may contain a backslash, as
            // \t(0,1000,\fs40) does, and stopping at the first one would cut the times it is being read for.
            ReadOnlySpan<char> argument;
            if (index < block.Length && block[index] == '(')
            {
                int close = block[index..].IndexOf(')');
                if (close < 0)
                {
                    return;
                }

                argument = block[index..(index + close + 1)];
                index += close + 1;
            }
            else
            {
                int argumentStart = index;
                while (index < block.Length && block[index] != '\\')
                {
                    index++;
                }

                argument = block[argumentStart..index];
            }

            if (name.SequenceEqual("t"))
            {
                // \t(<t1>,<t2>[,<accel>,<style modifiers>]) animates between its first two numbers; the acceleration-first
                // form starts with a fraction and does not fit that shape, so it falls back to the whole line.
                if (TryReadTuple(argument, 0, 1, out double from, out double to))
                {
                    Add(ref changes, start + TimeSpan.FromMilliseconds(from), start + TimeSpan.FromMilliseconds(to));
                }
                else
                {
                    AddWholeCue(ref changes, start, end);
                }
            }
            else if (name.SequenceEqual("move"))
            {
                // \move(<x1>,<y1>,<x2>,<y2>[,<t1>,<t2>]): the times are the fifth and sixth numbers, and their absence
                // means the line moves over its whole life.
                if (TryReadTuple(argument, 4, 5, out double from, out double to))
                {
                    Add(ref changes, start + TimeSpan.FromMilliseconds(from), start + TimeSpan.FromMilliseconds(to));
                }
                else
                {
                    AddWholeCue(ref changes, start, end);
                }
            }
            else if (name.SequenceEqual("fad") || name.SequenceEqual("fade") || name.SequenceEqual("fadecomplex"))
            {
                // A fade's tail is measured from the cue's end, so it is only knowable once that end is; an open cue is
                // taken as fading throughout rather than guessed at.
                AddWholeCue(ref changes, start, end);
            }
            else if (name.SequenceEqual("k") || name.SequenceEqual("K") || name.SequenceEqual("kf") || name.SequenceEqual("ko"))
            {
                // Karaoke times are durations in hundredths of a second, one per syllable, and they accumulate.
                if (double.TryParse(argument, out double centiseconds) && centiseconds > 0)
                {
                    karaokeOffset += TimeSpan.FromMilliseconds(centiseconds * 10.0);
                    (karaoke ??= []).Add(start + karaokeOffset);
                }
            }
        }
    }

    /// <summary>
    /// Reads two comma-separated fields of a parenthesised argument as a time pair. Anything that does not fit — a bare
    /// modifier list, an acceleration in the first position — is refused, because being wrong here either freezes an
    /// animation or redraws a static line sixty times a second.
    /// </summary>
    private static bool TryReadTuple(ReadOnlySpan<char> argument, int firstIndex, int secondIndex, out double from, out double to)
    {
        from = 0;
        to = 0;

        argument = argument.Trim();
        if (argument.Length < 2 || argument[0] != '(' || argument[^1] != ')')
        {
            return false;
        }

        ReadOnlySpan<char> inner = argument[1..^1];
        Span<Range> fields = stackalloc Range[8];
        int count = inner.Split(fields, ',');
        if (count > fields.Length || count <= secondIndex)
        {
            return false;
        }

        if (!double.TryParse(inner[fields[firstIndex]].Trim(), out from) || !double.TryParse(inner[fields[secondIndex]].Trim(), out to))
        {
            return false;
        }

        return to > from;
    }

    private static void AddWholeCue(ref List<SubtitleChange>? changes, TimeSpan start, TimeSpan end)
    {
        if (end == SubtitleCue.OpenEnd || end <= start)
        {
            return;
        }

        Add(ref changes, start, end);
    }

    private static void Add(ref List<SubtitleChange>? changes, TimeSpan from, TimeSpan to)
    {
        if (to <= from)
        {
            return;
        }

        (changes ??= []).Add(new SubtitleChange(from, to, true));
    }
}
