// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Win2D;

/// <summary>
/// Offers <see cref="Win2DSubtitleRenderer"/>s for text cues. Bitmap cues are refused: they need the geometry of the
/// image — width, height and stride — which a decoded cue does not carry yet, so offering a renderer for them would be a
/// promise the first drawing could not keep.
/// </summary>
public sealed class Win2DSubtitleRendererFactory : ISubtitleRendererFactory
{
    public OperationResult TryCreate(SubtitleCueFormat format, out IComponentCreator<ISubtitleRenderer>? creator)
    {
        creator = null;

        if (format != SubtitleCueFormat.Ass)
        {
            return OperationResult.Failure($"This renderer draws text subtitles, not {format} cues.");
        }

        creator = new Creator();
        return OperationResult.Success();
    }

    /// <summary>
    /// The format is part of the signature even though only one is accepted: a renderer that draws a different kind of
    /// cue later must not be mistaken for this one when the pipeline decides whether a running chain can be kept.
    /// </summary>
    private sealed class Creator : IComponentCreator<ISubtitleRenderer>
    {
        public string Signature => "win2d:subtitle:ass";

        public OperationResult Create(out ISubtitleRenderer? component)
        {
            component = new Win2DSubtitleRenderer();
            return OperationResult.Success();
        }
    }
}
