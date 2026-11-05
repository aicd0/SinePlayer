// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class AudioChain
{
    public ComponentSlot<IAudioDecoder> Decoder { get; } = new();

    public ComponentSlot<IAudioRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public AudioQueue BufferQueue { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public AudioFilterChain Filters { get; } = new();
}
