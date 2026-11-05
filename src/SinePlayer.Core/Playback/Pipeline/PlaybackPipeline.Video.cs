// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Concurrent;
using System.Diagnostics;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline
{
    #region Video decode

    private void TryWakeVideoDecode()
    {
        if (!HasRoomForVideoFrame())
        {
            return;
        }

        if (!_video.PacketQueue.IsEmpty)
        {
            _video.DecodeNode.Wake();
        }
    }

    private void VideoDecodeCore()
    {
        PacketQueue queue = _video.PacketQueue;
        ComponentSlot<IVideoDecoder> videoDecoderSlot = _video.Decoder;
        bool awaitingKeyFrame = false;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _video.DecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install video decoder
            if (videoDecoderSlot.Update(out IVideoDecoder? oldVideoDecoder))
            {
                oldVideoDecoder?.Dispose();
                awaitingKeyFrame = true;
            }

            IVideoDecoder? videoDecoder = videoDecoderSlot.CurrentComponent;

            // Bump epoch
            long epoch = _timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    videoDecoder?.Flush();
                }
                catch (Exception exception)
                {
                    _events.PostFailure(exception.Message, exception);
                    return;
                }

                continue;
            }

            // Dequeue
            if (!HasRoomForVideoFrame())
            {
                _video.DecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                _video.DecodeNode.Wait(stamp);
                continue;
            }

            PipelineVersion videoDecoderVersions = videoDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareVideoDecoder(entry.Versions, videoDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                _video.DecodeNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                _video.DecodeNode.Wait(stamp);
                continue;
            }

            TryWakeReader();

            // Decode
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
                        OperationResult drained = DrainVideoDecoder(videoDecoder, videoFrameVersions, packetEpoch);
                        if (!drained.IsSuccessful)
                        {
                            _events.PostWarning(drained.Message);
                        }
                    }

                    _video.FrameQueue.Enqueue(new FrameEntry(null, videoFrameVersions, epoch));
                    TryWakeVideoRender();
                    _video.DecodeNode.Wait(stamp);
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

                OperationResult decoded = DecodeVideo(videoDecoder, packet, out IReadOnlyList<RawVideoFrame>? frames);
                if (!decoded.IsSuccessful)
                {
                    _events.PostFailure(decoded);
                    return;
                }

                if (frames is null)
                {
                    _events.PostFailure("The video decoder returned no frames.");
                    return;
                }

                QueueFrames(frames, videoFrameVersions, epoch);
            }
        }
    }

    private OperationResult DecodeVideo(IVideoDecoder videoDecoder, MediaPacket packet, out IReadOnlyList<RawVideoFrame>? frames)
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

        Interlocked.Add(ref _video.DecodeTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!decoded.IsSuccessful || produced is null)
        {
            return decoded;
        }

        frames = produced;
        Interlocked.Add(ref _video.DecodedFrameCount, produced.Count);
        return OperationResult.Success();
    }

    private OperationResult DrainVideoDecoder(IVideoDecoder decoder, PipelineVersion versions, long epoch)
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
            if (fastExit || _timeline.Epoch != epoch)
            {
                fastExit = true;
                frame.Dispose();
                continue;
            }

            _video.FrameQueue.Enqueue(new FrameEntry(frame, versions, epoch));
        }

        if (!fastExit)
        {
            TryWakeVideoRender();
        }
    }

    private bool HasRoomForVideoFrame()
    {
        return _video.FrameQueue.Count < FrameQueueCapacity;
    }

    #endregion

    #region Video render

    private void TryWakeVideoRender()
    {
        if (!_video.FrameQueue.IsEmpty)
        {
            _video.RenderNode.Wake();
        }
    }

    private void VideoRenderCore()
    {
        ConcurrentQueue<FrameEntry> queue = _video.FrameQueue;
        ComponentSlot<IVideoRenderer> videoRendererSlot = _video.Renderer;
        bool deviceAvailable = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan? startTime = null;
        TimeSpan? firstFrameTime = null;

        void DequeueAndDispose()
        {
            if (!queue.TryDequeue(out FrameEntry entry))
            {
                return;
            }

            entry.Frame?.Dispose();
            TryWakeVideoDecode();
        }

        while (true)
        {
            long stamp = _video.RenderNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install video renderer
            if (videoRendererSlot.Update(out IVideoRenderer? oldVideoRenderer))
            {
                if (oldVideoRenderer is not null)
                {
                    if (videoRendererSlot.CurrentComponent is null)
                    {
                        OperationResult cleared;
                        try
                        {
                            cleared = oldVideoRenderer.ClearSurface();
                        }
                        catch (Exception ex)
                        {
                            cleared = OperationResult.Failure(ex);
                        }

                        if (!cleared.IsSuccessful)
                        {
                            _events.PostWarning(cleared.Message);
                        }

                        Interlocked.Exchange(ref _video.PictureTicks, -1);
                    }

                    oldVideoRenderer.Dispose();
                }

                deviceAvailable = videoRendererSlot.CurrentComponent is not null;
                continue;
            }

            IVideoRenderer? videoRenderer = videoRendererSlot.CurrentComponent;

            // Bump epoch
            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                firstFrameTime = null;
                Interlocked.Exchange(ref _video.PictureTicks, -1);
            }

            // Refresh surface
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            VideoTransform transform = VideoTransform;

            VideoOrientation containerTransform = SelectedVideoStream?.Orientation ?? VideoOrientation.Identity;
            if (!containerTransform.IsIdentity)
            {
                transform = transform with { Orientation = transform.Orientation.Combine(containerTransform) };
            }

            if (deviceAvailable && videoRenderer is not null && state is MediaEngineState.Paused or MediaEngineState.Ended)
            {
                OperationResult refreshed;
                try
                {
                    refreshed = videoRenderer.RefreshSurface(transform);
                }
                catch (Exception ex)
                {
                    refreshed = OperationResult.Failure(ex);
                }

                if (!refreshed.IsSuccessful)
                {
                    _events.PostWarning(refreshed.Message);
                    deviceAvailable = false;
                }
            }

            // Pause the clock on pause or starvation
            FrameEntry? entry = queue.TryPeek(out FrameEntry head) ? head : null;
            TimeSpan position = _timeline.Position;
            bool offeringTheStill = state == MediaEngineState.Paused && !firstFrameTime.HasValue && endingEpoch != epoch;

            bool starved = _video.Decoder.HasComponent && endingEpoch != epoch && entry is null;
            starved = starved && !IsStreamSpent(epoch, position, ref _readerTailVideoTicks);
            starved = starved && (!firstFrameTime.HasValue || IsVideoStarved(position));

            lock (_timeline.ClockLock)
            {
                _timeline.VideoStarved = starved;
                _timeline.StartOrPause(epoch, state);
            }

            // Idle on pause or starvation
            if (!playing && !offeringTheStill)
            {
                _video.RenderNode.Wait(stamp);
                continue;
            }

            if (entry is null)
            {
                _video.RenderNode.Wait(stamp, playing ? VideoIdleTimeoutMilliseconds : Timeout.Infinite);
                continue;
            }

            // Probe the entry
            if (entry.Value.Epoch != epoch)
            {
                DequeueAndDispose();
                continue;
            }

            VersionVerdict verdict = PipelineVersion.CompareVideoRenderer(entry.Value.Versions, videoRendererSlot.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                _video.RenderNode.Wait(stamp);
                continue;
            }

            if (verdict == VersionVerdict.Drop)
            {
                DequeueAndDispose();
                continue;
            }

            RawVideoFrame? frame = entry.Value.Frame;
            if (frame is null)
            {
                if (endingEpoch != entry.Value.Epoch)
                {
                    endingEpoch = entry.Value.Epoch;

                    long epochCopy = entry.Value.Epoch;
                    _events.Submit(() => NotifyVideoRenderEnded(epochCopy));
                }

                DequeueAndDispose();
                continue;
            }

            if (startTime.HasValue && frame.Timestamp < startTime)
            {
                DequeueAndDispose();
                continue;
            }

            // Ready to present
            FrameSyncAction syncAction = Synchronizer.Evaluate(frame.Timestamp, position);

            if (playing)
            {
                TimeSpan anchor = firstFrameTime ?? frame.Timestamp;

                lock (_timeline.ClockLock)
                {
                    if (_timeline.Anchor(epoch, anchor))
                    {
                        position = _timeline.Position;
                        syncAction = Synchronizer.Evaluate(frame.Timestamp, position);
                    }

                    _timeline.StartOrPause(epoch, state);
                }
            }

            if (playing && syncAction == FrameSyncAction.Wait)
            {
                int remaining = (int)Math.Clamp((frame.Timestamp - position).TotalMilliseconds, 1, VideoIdleTimeoutMilliseconds);
                _video.RenderNode.Wait(stamp, remaining);
                continue;
            }

            // Present
            if (!queue.TryDequeue(out _))
            {
                continue;
            }

            TryWakeVideoDecode();

            using (frame)
            {
                if (playing
                    && syncAction == FrameSyncAction.Drop
                    && queue.TryPeek(out FrameEntry closer)
                    && closer.Frame is not null
                    && closer.Epoch == epoch)
                {
                    Interlocked.Increment(ref _video.DroppedFrameCount);
                    continue;
                }

                if (deviceAvailable && videoRenderer is not null)
                {
                    if (playing && firstFrameTime.HasValue && !videoRenderer.TryAcquireFrameSlot())
                    {
                        Interlocked.Increment(ref _video.DroppedFrameCount);
                        continue;
                    }

                    OperationResult shown = RenderVideoFrame(videoRenderer, frame, transform);
                    if (!shown.IsSuccessful)
                    {
                        _events.PostWarning(shown.Message);
                        deviceAvailable = false;
                    }
                }

                if (!firstFrameTime.HasValue)
                {
                    firstFrameTime = frame.Timestamp;
                }
            }
        }
    }

    private OperationResult RenderVideoFrame(IVideoRenderer renderer, RawVideoFrame frame, VideoTransform transform)
    {
        long startTicks = Stopwatch.GetTimestamp();
        OperationResult rendered;
        try
        {
            rendered = renderer.Render(frame, transform);
        }
        catch (Exception ex)
        {
            rendered = OperationResult.Failure(ex.Message);
        }

        Interlocked.Add(ref _video.RenderTicks, Stopwatch.GetTimestamp() - startTicks);

        if (!rendered.IsSuccessful)
        {
            return rendered;
        }

        Interlocked.Increment(ref _video.PresentedFrameCount);
        Interlocked.Exchange(ref _video.PictureTicks, frame.Timestamp.Ticks);

        return OperationResult.Success();
    }

    private bool IsVideoStarved(TimeSpan position)
    {
        long presentedTicks = Interlocked.Read(ref _video.PictureTicks);
        if (presentedTicks < 0)
        {
            return false;
        }

        TimeSpan behind = position - TimeSpan.FromTicks(presentedTicks);
        return _timeline.VideoStarved
            ? behind > TimeSpan.FromMilliseconds(VideoStarvationResumeMilliseconds)
            : behind > TimeSpan.FromMilliseconds(VideoStarvationMilliseconds);
    }

    #endregion
}
