// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class VideoChain(PipelineTimeline timeline, PipelineEvents events) : IPictureFrameSource
{
    private const int FrameQueueCapacity = 8;
    public const int DecoderBufferedFrames = FrameQueueCapacity + 4;

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

    public void Decode(Func<bool> stopRequested, Action wakeReader)
    {
        bool awaitingKeyFrame = false;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = DecodeNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Decoder.Update(out IVideoDecoder? oldVideoDecoder))
            {
                oldVideoDecoder?.Dispose();
                awaitingKeyFrame = true;
            }

            IVideoDecoder? videoDecoder = Decoder.CurrentComponent;

            long epoch = timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    videoDecoder?.Flush();
                }
                catch (Exception exception)
                {
                    events.PostFailure(exception.Message, exception);
                    return;
                }

                continue;
            }

            if (!HasRoomForFrame())
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            PipelineVersion videoDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareVideoDecoder(entry.Versions, videoDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            if (!PacketQueue.TryDequeue(out _))
            {
                DecodeNode.Wait(stamp);
                continue;
            }

            wakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var videoFrameVersions = PipelineVersion.Max(entry.Versions, videoDecoderVersions);
                if (packet is null)
                {
                    if (videoDecoder is not null)
                    {
                        OperationResult drained = DrainDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                        if (!drained.IsSuccessful)
                        {
                            events.PostWarning(drained.Message);
                        }
                    }

                    FrameQueue.Enqueue(new FrameEntry(null, videoFrameVersions, epoch));
                    WakeRender();
                    DecodeNode.Wait(stamp);
                    continue;
                }

                if (awaitingKeyFrame)
                {
                    if (!packet.IsKeyFrame)
                    {
                        continue;
                    }

                    awaitingKeyFrame = false;
                }

                if (videoDecoder is null)
                {
                    continue;
                }

                OperationResult decoded = DecodePacket(videoDecoder, packet, out IReadOnlyList<RawVideoFrame>? frames);
                if (!decoded.IsSuccessful)
                {
                    events.PostFailure(decoded);
                    return;
                }

                if (frames is null)
                {
                    events.PostFailure("The video decoder returned no frames.");
                    return;
                }

                QueueFrames(frames, videoFrameVersions, epoch);
            }
        }
    }

    private OperationResult DecodePacket(IVideoDecoder videoDecoder, MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
    {
        frames = null;

        long startTicks = Stopwatch.GetTimestamp();
        OperationResult decoded;
        IReadOnlyList<RawVideoFrame>? produced = null;
        try
        {
            decoded = videoDecoder.TryDecode(packet, out produced);
        }
        catch (Exception ex)
        {
            decoded = OperationResult.Failure(ex);
        }

        Interlocked.Add(ref DecodeTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!decoded.IsSuccessful || produced is null)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref DecodedFrameCount, produced.Count);
        return OperationResult.Success();
    }

    private OperationResult DrainDecoder(IVideoDecoder decoder, PipelineVersion versions, long epoch)
    {
        OperationResult drained;
        IReadOnlyList<RawVideoFrame> frames;
        try
        {
            drained = decoder.Drain(out frames);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (frames is null)
        {
            return OperationResult.Failure("The video decoder returned no frames.");
        }

        QueueFrames(frames, versions, epoch);
        return OperationResult.Success();
    }

    private void QueueFrames(IReadOnlyList<RawVideoFrame> frames, PipelineVersion versions, long epoch)
    {
        bool fastExit = false;

        foreach (RawVideoFrame frame in frames)
        {
            if (fastExit || timeline.Epoch != epoch)
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            FrameQueue.Enqueue(new FrameEntry(frame, versions, epoch));
        }

        if (!fastExit)
        {
            WakeRender();
        }
    }

    public bool HasRoomForFrame()
    {
        return FrameQueue.Count < FrameQueueCapacity;
    }

    private void WakeRender()
    {
        if (!FrameQueue.IsEmpty)
        {
            RenderNode.Wake();
        }
    }

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
