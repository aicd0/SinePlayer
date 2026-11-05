// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class SubtitleChain
{
    public ComponentSlot<ISubtitleDecoder> Decoder { get; } = new();

    public ComponentSlot<ISubtitleRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public SubtitleCueWindow CueWindow { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public SeqLock<SubtitlePresentation> Presentation { get; } = new(SubtitlePresentation.Identity);

    public IVideoRenderTarget? Target { get; set; }

    public IPictureFrameSource? LayoutSource { get; set; }
}
