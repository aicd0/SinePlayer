// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public sealed partial class SubtitleCue : IDisposable
{
    public static readonly TimeSpan OpenEnd = TimeSpan.MaxValue;

    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; set; }

    public string? Text { get; init; }

    public SubtitleImage? Image { get; init; }

    public SubtitleChange[]? Changes { get; init; }

    public bool IsOpenEnded => End == OpenEnd;

    public void Dispose()
    {
        Image?.Dispose();
    }

    public bool HasChanges(TimeSpan drawn, TimeSpan position)
    {
        if (Changes is not { Length: > 0 } changes)
        {
            return false;
        }

        foreach (SubtitleChange change in changes)
        {
            if (change.Start <= position && change.End > drawn)
            {
                return true;
            }
        }

        return false;
    }

    public TimeSpan? TimeUntilChange(TimeSpan position)
    {
        if (Changes is not { Length: > 0 } changes)
        {
            return null;
        }

        TimeSpan? next = null;
        foreach (SubtitleChange change in changes)
        {
            if (change.End < position)
            {
                continue;
            }

            TimeSpan wait = change.Start > position ? change.Start - position : TimeSpan.Zero;
            if (next is null || wait < next.Value)
            {
                next = wait;
            }
        }

        return next;
    }
}
