// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class AudioChain(PipelineTimeline timeline, PipelineEvents events) : IChainFeed
{
    private const int AudioIdleTimeoutMilliseconds = 5;
    private const int AudioDeviceLeewayMilliseconds = 3000;
    private const int FallbackFrameMilliseconds = 40;

    private TimeSpan _lastPacketSpan = TimeSpan.Zero;

    public ComponentSlot<IAudioDecoder> Decoder { get; } = new();

    public ComponentSlot<IAudioRenderer> Renderer { get; } = new();

    public PacketQueue PacketQueue { get; } = new();

    public AudioQueue BufferQueue { get; } = new();

    public PipelineNodeAsync DecodeNode { get; } = new();

    public PipelineNode RenderNode { get; } = new();

    public AudioFilterChain Filters { get; } = new();

    public StreamBinding<AudioStreamInfo> Binding { get; set; }

    public ChainTail Tail { get; } = new();

    public bool IsActive => Binding.Stream is not null && Decoder.HasComponent;

    public bool TryAccept(MediaSource source, MediaPacket packet, PipelineVersion versions, long epoch)
    {
        if (!Binding.Accepts(source, packet.StreamIndex))
        {
            return false;
        }

        TimeSpan span = SpanOf(packet);
        _lastPacketSpan = span;
        PacketQueue.Enqueue(new PacketEntry(packet, versions, epoch, span));
        WakeDecode();
        return true;
    }

    public void SignalEnd(PipelineVersion versions, long epoch)
    {
        PacketQueue.Enqueue(new PacketEntry(null, versions, epoch, TimeSpan.Zero));
        WakeDecode();
    }

    private TimeSpan SpanOf(MediaPacket packet)
    {
        if (packet.Duration > TimeSpan.Zero)
        {
            return packet.Duration;
        }

        return _lastPacketSpan > TimeSpan.Zero ? _lastPacketSpan : TimeSpan.FromMilliseconds(FallbackFrameMilliseconds);
    }

    public async Task Decode(Func<bool> stopRequested, Action wakeReader)
    {
        long workingEpoch = -1;

        while (true)
        {
            long stamp = DecodeNode.Stamp;
            if (stopRequested())
            {
                return;
            }

            if (Decoder.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            long epoch = timeline.Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    Decoder.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    events.PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                }

                continue;
            }

            if (!PacketQueue.TryPeek(out PacketEntry entry))
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion audioDecoderVersions = Decoder.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareAudioDecoder(entry.Versions, audioDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await DecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!PacketQueue.TryDequeue(out _))
            {
                await DecodeNode.WaitAsync(stamp);
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

                var audioBufferVersions = PipelineVersion.Max(entry.Versions, audioDecoderVersions);
                IAudioDecoder? audioDecoder = Decoder.CurrentComponent;
                if (packet is null)
                {
                    OperationResult drained = DrainDecoder(audioDecoder, audioBufferVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        events.PostWarning(drained.Message);
                    }

                    BufferQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                    WakeRender();
                    continue;
                }

                if (audioDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodePacket(audioDecoder, packet, audioBufferVersions, epoch);
                if (!routed.IsSuccessful)
                {
                    events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodePacket(IAudioDecoder audioDecoder, MediaPacket packet, PipelineVersion versions, long epoch)
    {
        OperationResult decoded;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            decoded = audioDecoder.TryDecode(packet, out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!decoded.IsSuccessful)
        {
            return decoded;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
        }

        if (buffers.Count > 0)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private OperationResult DrainDecoder(IAudioDecoder? decoder, PipelineVersion versions, long epoch)
    {
        if (decoder is null)
        {
            return OperationResult.Success();
        }

        OperationResult drained;
        IReadOnlyList<RawAudioBuffer> buffers;
        try
        {
            drained = decoder.Drain(out buffers);
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(ex.Message);
        }

        if (!drained.IsSuccessful)
        {
            return drained;
        }

        if (buffers is null)
        {
            return OperationResult.Failure("The audio decoder returned no buffers.");
        }

        bool handedOver = false;
        foreach (RawAudioBuffer buffer in buffers)
        {
            BufferQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            WakeRender();
        }

        return OperationResult.Success();
    }

    private void WakeRender()
    {
        if (!BufferQueue.IsEmpty)
        {
            RenderNode.Wake();
        }
    }

    public void WakeDecode()
    {
        if (!PacketQueue.IsEmpty)
        {
            DecodeNode.Wake();
        }
    }

    public void Render(Func<bool> stopRequested, Func<long, TimeSpan, bool> isStreamSpent, Action<long> reportEnded)
    {
        bool deviceAvailable = false;
        bool reportedRunningAhead = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        long playedOutEpoch = -1;
        TimeSpan? clockOffset = null;
        TimeSpan? startTime = null;
        TimeSpan? tailBufferPosition = null;
        AudioStreamInfo? deviceFormat = null;
        AudioFilterChainSnapshot? filters = null;

        IAudioSubmit? inFlightSubmit = null;
        RawAudioBuffer? inFlightBuffer = null;

        void ReleaseInFlightAudio()
        {
            inFlightSubmit?.Dispose();
            inFlightSubmit = null;
            inFlightBuffer?.Dispose();
            inFlightBuffer = null;
        }

        bool AbandonFailedDevice(IAudioRenderer? renderer)
        {
            deviceAvailable = false;

            if (renderer is null || renderer.IsDeviceAvailable)
            {
                return false;
            }

            Renderer.Publish(null, Renderer.CurrentVersions);
            return true;
        }

        while (true)
        {
            long stamp = RenderNode.Stamp;
            if (stopRequested())
            {
                break;
            }

            if (Renderer.Update(out IAudioRenderer? oldAudioRenderer))
            {
                ReleaseInFlightAudio();
                oldAudioRenderer?.Flush();
                oldAudioRenderer?.Dispose();

                deviceAvailable = false;
                reportedRunningAhead = false;
                clockOffset = null;
                deviceFormat = null;
                filters = null;
                continue;
            }

            IAudioRenderer? audioRenderer = Renderer.CurrentComponent;

            if (filters is null || filters.Version != Filters.Version)
            {
                filters = Filters.Snapshot();

                AudioStreamInfo? outputFormat = filters.OutputFormat;
                if (audioRenderer is not null && outputFormat is not null && (deviceFormat is null || !AudioFilterChain.IsSameFormat(outputFormat, deviceFormat)))
                {
                    ReleaseInFlightAudio();

                    OperationResult opened = audioRenderer.Configure(outputFormat);
                    if (opened.IsSuccessful)
                    {
                        deviceFormat = outputFormat;
                        deviceAvailable = true;
                    }
                    else
                    {
                        deviceFormat = null;
                        events.PostWarning($"The audio device could not be opened: {opened.Message}");
                        if (AbandonFailedDevice(audioRenderer))
                        {
                            continue;
                        }
                    }

                    clockOffset = null;
                }
            }

            EpochGate epochGate = timeline.Gate;
            long epoch = epochGate.Epoch;
            bool epochChanged = workingEpoch != epoch;

            if (epochChanged)
            {
                workingEpoch = epoch;
                startTime = epochGate.SeekMode == SeekMode.Precise ? epochGate.SeekPosition : null;
                tailBufferPosition = null;
                reportedRunningAhead = false;
                audioRenderer?.Flush();
            }

            if (epochChanged || !deviceAvailable)
            {
                clockOffset = null;
                ReleaseInFlightAudio();
            }

            bool playing = events.CheckState(out MediaEngineState state, MediaEngineState.Playing);
            lock (timeline.ClockLock)
            {
                if (epochChanged)
                {
                    timeline.AudioStarved = true;
                }

                timeline.StartOrPause(epoch, state);

                if (deviceAvailable && audioRenderer is not null)
                {
                    IPlaybackClock deviceClock = audioRenderer.Clock;
                    bool clockRunning = timeline.IsRunning;
                    if (clockRunning != deviceClock.IsRunning)
                    {
                        try
                        {
                            if (clockRunning)
                            {
                                deviceClock.Start();
                            }
                            else
                            {
                                deviceClock.Pause();
                            }
                        }
                        catch (Exception ex)
                        {
                            events.PostWarning(ex.Message);
                            AbandonFailedDevice(audioRenderer);
                            continue;
                        }
                    }
                }
            }

            if (!playing)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            TimeSpan position = timeline.Position;
            if (deviceAvailable && tailBufferPosition is TimeSpan consumed
                && consumed - position > TimeSpan.FromMilliseconds(AudioDeviceLeewayMilliseconds))
            {
                if (!reportedRunningAhead)
                {
                    reportedRunningAhead = true;
                    events.PostWarning("The audio device is not playing what it is given.");
                }

                lock (timeline.ClockLock)
                {
                    // A device that is running ahead of the clock is not starved.
                    timeline.AudioStarved = false;
                }

                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            bool hasDeviceAudio = deviceAvailable && audioRenderer is not null && audioRenderer.QueuedSampleCount > 0;
            if (deviceAvailable && audioRenderer is not null)
            {
                IPlaybackClock deviceClock = audioRenderer.Clock;

                if (hasDeviceAudio && clockOffset is TimeSpan offset)
                {
                    timeline.SyncClock(deviceClock, offset);
                }

                if (inFlightSubmit is not null)
                {
                    if (!inFlightSubmit.MoveNext())
                    {
                        OperationResult submitted = inFlightSubmit.Result;
                        ReleaseInFlightAudio();

                        if (!submitted.IsSuccessful)
                        {
                            events.PostWarning(submitted.Message);
                            AbandonFailedDevice(audioRenderer);
                        }

                        continue;
                    }

                    RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                    continue;
                }
            }

            if (BufferQueue.IsEmpty)
            {
                bool ending = endingEpoch == epoch;
                bool starved = deviceAvailable && !hasDeviceAudio && !ending && !isStreamSpent(epoch, position);
                lock (timeline.ClockLock)
                {
                    timeline.AudioStarved = starved;
                    timeline.StartOrPause(epoch, state);
                }

                if (ending && !hasDeviceAudio && playedOutEpoch != endingEpoch)
                {
                    playedOutEpoch = endingEpoch;

                    long epochCopy = endingEpoch;
                    events.Submit(() => reportEnded(epochCopy));
                }

                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Idle on failing device if the clock is behind, since the device clock is no longer reliable
            if (!deviceAvailable && tailBufferPosition.HasValue && position < tailBufferPosition.Value)
            {
                RenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            if (!BufferQueue.TryPeek(out AudioEntry entry))
            {
                continue;
            }

            VersionVerdict verdict = PipelineVersion.CompareAudioRenderer(entry.Versions, Renderer.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                RenderNode.Wait(stamp);
                continue;
            }

            if (!BufferQueue.TryDequeue(out _))
            {
                continue;
            }

            RawAudioBuffer? buffer = entry.Buffer;
            long bufferEpoch = entry.Epoch;

            if (verdict == VersionVerdict.Drop)
            {
                buffer?.Dispose();
                continue;
            }

            if (epoch != bufferEpoch)
            {
                buffer?.Dispose();
                continue;
            }

            if (buffer is null)
            {
                endingEpoch = entry.Epoch;
                continue;
            }

            if (startTime.HasValue && buffer.Timestamp < startTime)
            {
                buffer.Dispose();
                continue;
            }

            OperationResult<RawAudioBuffer> filtersApplied = filters.TryApply(buffer);
            if (!filtersApplied.IsSuccessful)
            {
                buffer.Dispose();
                events.PostFailure(filtersApplied.Message, filtersApplied.Exception);
                return;
            }

            buffer = filtersApplied.Result;

            tailBufferPosition = buffer.Timestamp + buffer.Duration;

            lock (timeline.ClockLock)
            {
                timeline.AudioStarved = false;

                bool clockChanged = timeline.Anchor(epoch, buffer.Timestamp);
                if (deviceAvailable && audioRenderer is not null && (clockChanged || clockOffset is null))
                {
                    clockOffset = audioRenderer.Clock.Position - buffer.Timestamp;
                }

                timeline.StartOrPause(epoch, state);
            }

            if (deviceAvailable && audioRenderer is not null)
            {
                inFlightSubmit = audioRenderer.Submit(buffer);
                inFlightBuffer = buffer;
            }
            else
            {
                buffer.Dispose();
            }
        }

        ReleaseInFlightAudio();
    }
}
