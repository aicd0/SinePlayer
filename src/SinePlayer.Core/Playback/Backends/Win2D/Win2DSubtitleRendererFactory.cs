// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Win2D;

public sealed class Win2DSubtitleRendererFactory : ISubtitleRendererFactory
{
    public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleCueFormat format)
    {
        OperationResultCreator<IComponentCreator<ISubtitleRenderer>> result = new();

        if (format is not (SubtitleCueFormat.Ass or SubtitleCueFormat.BitmapBgra))
        {
            return result.Failure($"This renderer draws text and bitmap subtitles, not {format} cues.");
        }

        return result.Success(new Creator(format));
    }

    private sealed class Creator(SubtitleCueFormat format) : IComponentCreator<ISubtitleRenderer>
    {
        public string Signature => format == SubtitleCueFormat.BitmapBgra ? "win2d:subtitle:bitmap" : "win2d:subtitle:ass";

        public OperationResult Create(out ISubtitleRenderer? component)
        {
            component = new Win2DSubtitleRenderer();
            return OperationResult.Success();
        }
    }
}
