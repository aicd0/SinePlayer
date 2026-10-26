// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A subtitle renderer that draws nothing. It records what it was asked to draw instead, so the chain still runs as it
/// would with a real renderer — cues arrive, visibility changes at cue boundaries, a seek replaces the set — while
/// nothing needs a window, a panel or a graphics device.
/// <para>
/// <see cref="ShowCount"/> is what a test or the Lab watches to see that a switch happened: after a replacement the new
/// instance starts from zero and the old one stops counting. It counts calls rather than cues because the pipeline only
/// calls when something changed, so a count that runs away would mean it is redrawing the same thing.
/// </para>
/// </summary>
public sealed partial class NullSubtitleRenderer : ISubtitleRenderer
{
    private readonly Lock _lock = new();

    private long _showCount;
    private string? _shownText;
    private SubtitlePlacement _placement;

    public string Name => "Null";

    /// <summary>How many times this renderer has been asked to show a set of cues since it was created.</summary>
    public long ShowCount => Interlocked.Read(ref _showCount);

    /// <summary>What was last shown, or null when the screen was last cleared.</summary>
    public string? ShownText
    {
        get
        {
            lock (_lock)
            {
                return _shownText;
            }
        }
    }

    /// <summary>The placement that came with the last shown set, which is how the pipeline's adjustment can be read back.</summary>
    public SubtitlePlacement Placement
    {
        get
        {
            lock (_lock)
            {
                return _placement;
            }
        }
    }

    public OperationResult Configure(SubtitleStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);
        return OperationResult.Success();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, SubtitlePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(cues);

        // The cues are the pipeline's and valid only for this call, so what is kept is a copy of the text.
        List<string> texts = [];
        foreach (SubtitleCue cue in cues)
        {
            texts.Add(cue.Text ?? string.Empty);
        }

        string? combined = texts.Count == 0 ? null : string.Join("\n", texts);
        lock (_lock)
        {
            _shownText = combined;
            _placement = placement;
        }

        Interlocked.Increment(ref _showCount);
        return OperationResult.Success();
    }

    public void Dispose()
    {
    }
}
