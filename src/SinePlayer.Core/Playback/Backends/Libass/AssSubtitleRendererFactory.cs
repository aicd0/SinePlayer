// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Libass;

/// <summary>
/// Offers libass for ASS and SSA scripts, and for nothing else.
/// <para>
/// The format alone cannot make that choice: every text track reaches a renderer as ASS event lines, so a SubRip track and
/// an ASS one are indistinguishable by cue format. What tells them apart is the stream, and this factory is where the two
/// routes separate — a script with its own styles and layout goes to the renderer that implements that language, and
/// everything else stays with the one that draws a line of text well.
/// </para>
/// </summary>
public sealed class AssSubtitleRendererFactory : ISubtitleRendererFactory
{
    private const string Signature = "libass:subtitle:ass";

    /// <summary>Whether the codec is one of the two the script format is carried in, as the container names them.</summary>
    private static bool IsAssScript(string? codecId)
    {
        return string.Equals(codecId, "ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codecId, "ssa", StringComparison.OrdinalIgnoreCase);
    }

    public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleStreamInfo stream, SubtitleCueFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);

        OperationResultCreator<IComponentCreator<ISubtitleRenderer>> result = new();

        if (format != SubtitleCueFormat.Ass)
        {
            return result.Failure($"This renderer draws ASS scripts, not {format} cues.");
        }

        if (!IsAssScript(stream.CodecId))
        {
            return result.Failure($"The {stream.CodecId} track is not an ASS or SSA script.");
        }

        return result.Success(new Creator());
    }

    private sealed class Creator : IComponentCreator<ISubtitleRenderer>
    {
        public string Signature => AssSubtitleRendererFactory.Signature;

        public OperationResult Create(out ISubtitleRenderer? component)
        {
            component = new AssSubtitleRenderer();
            return OperationResult.Success();
        }
    }
}
