// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class VideoChain
{
    public ComponentSlot<IVideoDecoder> Decoder { get; } = new();

    public ComponentSlot<IVideoRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public ConcurrentQueue<FrameEntry> FrameQueue { get; } = new();

    public PipelineNode DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public SeqLock<VideoTransform> Transform { get; } = new(VideoTransform.Identity);

    public IVideoRenderTarget? Target { get; set; }

    public long PresentedFrameCount;
    public long DroppedFrameCount;
    public long DecodedFrameCount;
    public long DecodeTicks;
    public long RenderTicks;
    public long PictureTicks = -1;
}
