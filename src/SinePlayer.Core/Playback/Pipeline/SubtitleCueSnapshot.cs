// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The cues that belong on screen at one position, held for as long as whoever asked for them needs them.
/// <para>
/// A cue owns a picture — a native buffer of premultiplied pixels — and the window that hands cues out is not the last
/// thing to touch one: a renderer may still be drawing the frame a cue was in when the playhead has moved past it. Who
/// is finished with a cue is therefore not something time can answer, so it is counted instead: the window holds a cue
/// while it can still be asked for, and every snapshot holds it too, and the picture is freed when the last of them lets
/// go. Disposing this is how a caller says it is done.
/// </para>
/// <para>
/// It is internal on purpose: <see cref="ISubtitleRenderer.Show"/> is handed the cues themselves, so a renderer is
/// written against a plain list and never has to know that anything is being counted.
/// </para>
/// </summary>
internal sealed class SubtitleCueSnapshot : IDisposable
{
    /// <summary>Nothing to draw, which is most positions in a file. Shared, and disposing it does nothing.</summary>
    internal static SubtitleCueSnapshot Empty { get; } = new([], null);

    /// <summary>
    /// Resolved while the references were held, so reading them after a release is not a way to reach freed memory —
    /// a released snapshot simply has nothing left to read them through.
    /// </summary>
    private readonly SubtitleCue[] _cues;

    /// <summary>The holds this snapshot owns, or null once it has been released.</summary>
    private RefCounted<SubtitleCue>[]? _holds;

    private SubtitleCueSnapshot(SubtitleCue[] cues, RefCounted<SubtitleCue>[]? holds)
    {
        _cues = cues;
        _holds = holds;
    }

    /// <summary>Takes ownership of holds the caller has already taken, one per cue, in the order given.</summary>
    internal static SubtitleCueSnapshot Lease(List<RefCounted<SubtitleCue>> holds)
    {
        if (holds.Count == 0)
        {
            return Empty;
        }

        var cues = new SubtitleCue[holds.Count];
        for (int index = 0; index < cues.Length; index++)
        {
            cues[index] = holds[index].Value;
        }

        return new SubtitleCueSnapshot(cues, [.. holds]);
    }

    internal IReadOnlyList<SubtitleCue> Cues => _cues;

    /// <summary>
    /// Lets go of every cue this snapshot was holding. Safe to call twice, and the shared empty snapshot is unaffected,
    /// so a caller has one release to make on every path rather than a rule about which paths own the cues.
    /// </summary>
    public void Dispose()
    {
        RefCounted<SubtitleCue>[]? holds = Interlocked.Exchange(ref _holds, null);
        if (holds is null)
        {
            return;
        }

        foreach (RefCounted<SubtitleCue> hold in holds)
        {
            hold.Unref();
        }
    }
}
