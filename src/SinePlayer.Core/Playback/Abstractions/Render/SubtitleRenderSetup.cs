// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Render;

public readonly record struct SubtitleRenderSetup
{
    public required SubtitleStreamInfo Stream { get; init; }

    public required IReadOnlyList<MediaAttachment> Attachments { get; init; }

    public required IReadOnlyList<string> FontPaths { get; init; }

    public required IVideoRenderTarget Target { get; init; }
}