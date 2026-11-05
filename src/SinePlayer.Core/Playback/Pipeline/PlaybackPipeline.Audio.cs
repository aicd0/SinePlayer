// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline
{
    #region Audio decode

    private void TryWakeAudioDecode()
    {
        if (!_audioPacketQueue.IsEmpty)
        {
            _audioDecodeNode.Wake();
        }
    }

    private async Task AudioDecodeCore()
    {
        PacketQueue queue = _audioPacketQueue;
        ComponentSlot<IAudioDecoder> audioDecoderSlot = _audioDecoder;
        long workingEpoch = -1;

        while (true)
        {
            long stamp = _audioDecodeNode.Stamp;
            if (_stopRequested)
            {
                return;
            }

            // Install audio decoder
            if (audioDecoderSlot.Update(out IAudioDecoder? oldAudioDecoder))
            {
                oldAudioDecoder?.Dispose();
            }

            // Bump epoch
            long epoch = _epochGate.Read().Epoch;
            if (workingEpoch != epoch)
            {
                workingEpoch = epoch;

                try
                {
                    audioDecoderSlot.CurrentComponent?.Flush();
                }
                catch (Exception ex)
                {
                    _events.PostWarning($"The audio decoder could not be flushed: {ex.Message}");
                }

                continue;
            }

            if (!queue.TryPeek(out PacketEntry entry))
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            PipelineVersion audioDecoderVersions = audioDecoderSlot.CurrentVersions;
            VersionVerdict verdict = PipelineVersion.CompareAudioDecoder(entry.Versions, audioDecoderVersions);
            if (verdict == VersionVerdict.Block)
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
            {
                await _audioDecodeNode.WaitAsync(stamp);
                continue;
            }

            TryWakeReader();

            MediaPacket? packet = entry.Packet;
            long packetEpoch = entry.Epoch;
            using (packet)
            {
                if (verdict == VersionVerdict.Drop || epoch != packetEpoch)
                {
                    continue;
                }

                var audioBufferVersions = PipelineVersion.Max(entry.Versions, audioDecoderVersions);
                IAudioDecoder? audioDecoder = audioDecoderSlot.CurrentComponent;
                if (packet is null)
                {
                    OperationResult drained = DrainAudioDecoder(audioDecoder, audioBufferVersions, packetEpoch);
                    if (!drained.IsSuccessful)
                    {
                        _events.PostWarning(drained.Message);
                    }

                    _audioQueue.Enqueue(new AudioEntry(null, audioBufferVersions, epoch));
                    TryWakeAudioRender();
                    continue;
                }

                if (audioDecoder is null)
                {
                    continue;
                }

                OperationResult routed = DecodeAudio(audioDecoder, packet, audioBufferVersions, epoch);
                if (!routed.IsSuccessful)
                {
                    _events.PostFailure(routed);
                    return;
                }
            }

            await Task.Yield();
        }
    }

    private OperationResult DecodeAudio(IAudioDecoder audioDecoder, MediaPacket packet, PipelineVersion versions, long epoch)
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
            _audioQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
        }

        if (buffers.Count > 0)
        {
            TryWakeAudioRender();
        }

        return OperationResult.Success();
    }

    private OperationResult DrainAudioDecoder(IAudioDecoder? decoder, PipelineVersion versions, long epoch)
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
            _audioQueue.Enqueue(new AudioEntry(buffer, versions, epoch));
            handedOver = true;
        }

        if (handedOver)
        {
            TryWakeAudioRender();
        }

        return OperationResult.Success();
    }

    #endregion

    #region Audio render

    private void TryWakeAudioRender()
    {
        if (!_audioQueue.IsEmpty)
        {
            _audioRenderNode.Wake();
        }
    }

    private void AudioRenderCore()
    {
        AudioQueue queue = _audioQueue;
        ComponentSlot<IAudioRenderer> audioRendererSlot = _audioRenderer;
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

            audioRendererSlot.Publish(null, audioRendererSlot.CurrentVersions);
            return true;
        }

        while (true)
        {
            long stamp = _audioRenderNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            // Install audio renderer
            if (audioRendererSlot.Update(out IAudioRenderer? oldAudioRenderer))
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

            IAudioRenderer? audioRenderer = audioRendererSlot.CurrentComponent;

            if (filters is null || filters.Version != AudioFilters.Version)
            {
                filters = AudioFilters.Snapshot();

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
                        _events.PostWarning($"The audio device could not be opened: {opened.Message}");
                        if (AbandonFailedDevice(audioRenderer))
                        {
                            continue;
                        }
                    }

                    clockOffset = null;
                }
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
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

            // Pause the clock on pause or starvation
            bool playing = CheckState(out MediaEngineState state, MediaEngineState.Playing);
            lock (_clockLock)
            {
                if (epochChanged)
                {
                    _audioStarved = true;
                }

                StartOrPauseClock(epoch, state);

                if (deviceAvailable && audioRenderer is not null)
                {
                    IPlaybackClock deviceClock = audioRenderer.Clock;
                    bool clockRunning = _clock.IsRunning;
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
                            _events.PostWarning(ex.Message);
                            AbandonFailedDevice(audioRenderer);
                            continue;
                        }
                    }
                }
            }

            // Idle on pause
            if (!playing)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            // Idle on leeway device
            TimeSpan position = _clock.Position;
            if (deviceAvailable && tailBufferPosition is TimeSpan consumed
                && consumed - position > TimeSpan.FromMilliseconds(AudioDeviceLeewayMilliseconds))
            {
                if (!reportedRunningAhead)
                {
                    reportedRunningAhead = true;
                    _events.PostWarning("The audio device is not playing what it is given.");
                }

                lock (_clockLock)
                {
                    // A device that is running ahead of the clock is not starved.
                    _audioStarved = false;
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Continue in-flight audio
            bool hasDeviceAudio = deviceAvailable && audioRenderer is not null && audioRenderer.QueuedSampleCount > 0;
            if (deviceAvailable && audioRenderer is not null)
            {
                IPlaybackClock deviceClock = audioRenderer.Clock;

                if (hasDeviceAudio && clockOffset is TimeSpan offset)
                {
                    SyncClock(_clock, deviceClock, offset);
                }

                if (inFlightSubmit is not null)
                {
                    if (!inFlightSubmit.MoveNext())
                    {
                        OperationResult submitted = inFlightSubmit.Result;
                        ReleaseInFlightAudio();

                        if (!submitted.IsSuccessful)
                        {
                            _events.PostWarning(submitted.Message);
                            AbandonFailedDevice(audioRenderer);
                        }

                        continue;
                    }

                    _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                    continue;
                }
            }

            // Empty queue: starvation or ending
            if (queue.IsEmpty)
            {
                bool ending = endingEpoch == epoch;
                bool starved = deviceAvailable && !hasDeviceAudio && !ending && !IsStreamSpent(epoch, position, ref _readerTailAudioTicks);
                lock (_clockLock)
                {
                    _audioStarved = starved;
                    StartOrPauseClock(epoch, state);
                }

                if (ending && !hasDeviceAudio && playedOutEpoch != endingEpoch)
                {
                    playedOutEpoch = endingEpoch;

                    long epochCopy = endingEpoch;
                    _events.Submit(() => NotifyAudioRenderEnded(epochCopy));
                }

                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Idle on failing device if the clock is behind, since the device clock is no longer reliable
            if (!deviceAvailable && tailBufferPosition.HasValue && position < tailBufferPosition.Value)
            {
                _audioRenderNode.Wait(stamp, AudioIdleTimeoutMilliseconds);
                continue;
            }

            // Probe the entry
            if (!queue.TryPeek(out AudioEntry entry))
            {
                continue;
            }

            VersionVerdict verdict = PipelineVersion.CompareAudioRenderer(entry.Versions, audioRendererSlot.CurrentVersions);
            if (verdict == VersionVerdict.Block)
            {
                _audioRenderNode.Wait(stamp);
                continue;
            }

            if (!queue.TryDequeue(out _))
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
                _events.PostFailure(filtersApplied.Message, filtersApplied.Exception);
                return;
            }

            buffer = filtersApplied.Result;

            // Ready to submit
            tailBufferPosition = buffer.Timestamp + buffer.Duration;

            lock (_clockLock)
            {
                _audioStarved = false;

                bool clockChanged = AnchorClock(epoch, buffer.Timestamp);
                if (deviceAvailable && audioRenderer is not null && (clockChanged || clockOffset is null))
                {
                    clockOffset = audioRenderer.Clock.Position - buffer.Timestamp;
                }

                StartOrPauseClock(epoch, state);
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

        // Cleanup
        ReleaseInFlightAudio();
    }

    private void SyncClock(IPlaybackClock clock, IPlaybackClock deviceClock, TimeSpan offset)
    {
        TimeSpan deviceTime;
        TimeSpan localTime;
        try
        {
            deviceTime = deviceClock.Position;
            localTime = clock.Position;
        }
        catch (Exception ex)
        {
            PlaybackLogger.E(TAG, ex);
            return;
        }

        TimeSpan expected = deviceTime - offset;
        if (Math.Abs((expected - localTime).TotalMilliseconds) <= ClockSyncToleranceMilliseconds)
        {
            return;
        }

        lock (_clockLock)
        {
            if (!clock.IsRunning || _anchoredEpoch != _epochGate.Read().Epoch)
            {
                return;
            }

            clock.SetPosition(expected);
            NotifyClockMoved();
        }
    }

    #endregion
}
