// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public interface ISubtitleRenderer : IDisposable
{
    string Name { get; }

    OperationResult Configure(SubtitleStreamInfo stream, IVideoRenderTarget target);

    OperationResult Show(IReadOnlyList<SubtitleCue> cues, SubtitlePlacement placement);
}
