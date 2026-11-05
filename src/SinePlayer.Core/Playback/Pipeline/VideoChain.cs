// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Numerics;

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class VideoChain : IPictureFrameSource
{
    public ComponentSlot<IVideoDecoder> Decoder { get; } = new();

    public ComponentSlot<IVideoRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public ConcurrentQueue<FrameEntry> FrameQueue { get; } = new();

    public PipelineNode DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public SeqLock<VideoTransform> Transform { get; } = new(VideoTransform.Identity);

    public IVideoRenderTarget? Target { get; set; }

    public VideoStreamInfo? Stream { get; set; }

    public long PresentedFrameCount;
    public long DroppedFrameCount;
    public long DecodedFrameCount;
    public long DecodeTicks;
    public long RenderTicks;
    public long PictureTicks = -1;

    public SubtitleFrame Frame
    {
        get
        {
            IVideoRenderTarget? target = Target;
            VideoStreamInfo? stream = Stream;
            if (target is null || stream is null || stream.Width <= 0 || stream.Height <= 0)
            {
                return SubtitleFrame.Full;
            }

            int surfaceWidth = target.SurfaceWidth;
            int surfaceHeight = target.SurfaceHeight;
            if (surfaceWidth <= 0 || surfaceHeight <= 0)
            {
                return SubtitleFrame.Full;
            }

            // The placement maps the picture's own square onto the surface, so its four corners say where the picture ends:
            // the bounding box of that is the frame, whatever turning, zooming or mirroring was asked for.
            Matrix3x2 placement = Transform.Read().GetPlacement(stream.Width, stream.Height, surfaceWidth, surfaceHeight);
            var zero = Vector2.Transform(Vector2.Zero, placement);
            var unitX = Vector2.Transform(Vector2.UnitX, placement);
            var unitY = Vector2.Transform(Vector2.UnitY, placement);
            var one = Vector2.Transform(Vector2.One, placement);

            float left = Math.Clamp(MathF.Min(MathF.Min(zero.X, unitX.X), MathF.Min(unitY.X, one.X)), 0.0f, 1.0f);
            float top = Math.Clamp(MathF.Min(MathF.Min(zero.Y, unitX.Y), MathF.Min(unitY.Y, one.Y)), 0.0f, 1.0f);
            float right = Math.Clamp(MathF.Max(MathF.Max(zero.X, unitX.X), MathF.Max(unitY.X, one.X)), 0.0f, 1.0f);
            float bottom = Math.Clamp(MathF.Max(MathF.Max(zero.Y, unitX.Y), MathF.Max(unitY.Y, one.Y)), 0.0f, 1.0f);

            // A picture larger than the surface is cropped by it, so what is left of it is the whole surface.
            return right > left && bottom > top
                ? new SubtitleFrame(left, top, right - left, bottom - top)
                : SubtitleFrame.Full;
        }
    }
}
