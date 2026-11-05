// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline
{
    #region Reader

    private void TryWakeReader()
    {
        MediaSourceHolder? sourceHolder = _source.Latest?.Component;
        if (sourceHolder is not null && !IsBuffered(sourceHolder, ReadAheadWake))
        {
            _readerNode.Wake();
        }
    }

    private async Task ReaderCore()
    {
        static TimeSpan SpanOf(MediaPacket? packet, TimeSpan previous, double frameRate)
        {
            if (packet is not null && packet.Duration > TimeSpan.Zero)
            {
                return packet.Duration;
            }

            if (previous > TimeSpan.Zero)
            {
                return previous;
            }

            if (!double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 1000)
            {
                return TimeSpan.FromMilliseconds(FallbackFrameMilliseconds);
            }

            return TimeSpan.FromSeconds(1 / frameRate);
        }

        ComponentSlot<MediaSourceHolder> sourceSlot = _source;
        bool pendingEpochChange = false;
        bool buffering = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        TimeSpan lastVideoPacketSpan = TimeSpan.Zero;
        TimeSpan lastAudioPacketSpan = TimeSpan.Zero;
        TimeSpan lastSubtitlePacketSpan = TimeSpan.Zero;
        Dictionary<MediaSource, SourceTail> tails = [];

        while (true)
        {
            long stamp = _readerNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            if (!CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Install source
            if (sourceSlot.Update(out MediaSourceHolder? oldSourceHolder))
            {
                oldSourceHolder?.Dispose();
                pendingEpochChange = true;
                tails.Clear();
            }

            MediaSourceHolder? sourceHolder = sourceSlot.CurrentComponent;
            if (sourceHolder is null)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            IReadOnlyList<MediaSource> sources = sourceHolder.Sources;
            if (sources.Count == 0)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Bump epoch
            EpochGate epochGate = _epochGate.Read();
            long epoch = epochGate.Epoch;
            if (epoch != workingEpoch)
            {
                workingEpoch = epoch;
                pendingEpochChange = false;
                tails.Clear();

                Volatile.Write(ref _readerTailEpoch, epoch);
                Interlocked.Exchange(ref _readerTailVideoTicks, -1);
                Interlocked.Exchange(ref _readerTailAudioTicks, -1);
                Interlocked.Exchange(ref _readerTailSubtitleTicks, -1);

                OperationResult sought = SeekSources(sourceHolder, epochGate.SeekPosition);
                if (sought.IsSuccessful)
                {
                    endingEpoch = -1;
                }
                else
                {
                    PostWarning(sought.Message);
                }

                continue;
            }

            if (pendingEpochChange || endingEpoch == epoch)
            {
                await _readerNode.WaitAsync(stamp);
                continue;
            }

            // Buffering
            if (!buffering)
            {
                if (IsBuffered(sourceHolder, ReadAheadWake))
                {
                    await _readerNode.WaitAsync(stamp);
                    continue;
                }

                buffering = true;
            }
            else if (IsBuffered(sourceHolder, ReadAheadMin))
            {
                buffering = false;
                continue;
            }

            MediaSource? chosenSource = null;
            SourceTail? chosenTail = null;
            foreach (MediaSource source in sources)
            {
                if (!tails.TryGetValue(source, out SourceTail? tail))
                {
                    tail = new SourceTail { Tail = epochGate.SeekPosition };
                    tails[source] = tail;
                }

                if (tail.Ended)
                {
                    continue;
                }

                if (chosenTail is null || tail.Tail < chosenTail.Tail)
                {
                    chosenSource = source;
                    chosenTail = tail;
                }
            }

            PipelineVersion sourceVersions = sourceSlot.CurrentVersions;
            if (chosenSource is null || chosenTail is null)
            {
                endingEpoch = epoch;

                _videoPacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _audioPacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _subtitlePacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                TryWakeVideoDecode();
                TryWakeAudioDecode();
                TryWakeSubtitleDecode();

                long endedEpoch = epoch;
                _ = Submit(() => NotifySourceEnded(endedEpoch));
                continue;
            }

            MediaPacket? packet = null;
            OperationResult read;
            try
            {
                read = chosenSource.Source.TryReadPacket(out packet);
            }
            catch (Exception ex)
            {
                read = OperationResult.Failure(ex.Message);
            }

            if (!read.IsSuccessful)
            {
                PostFailure(read);
                break;
            }

            if (packet is null)
            {
                chosenTail.Ended = true;
                continue;
            }

            if (packet.Timestamp > chosenTail.Tail)
            {
                chosenTail.Tail = packet.Timestamp;
            }

            // Route packet
            bool handedOn = false;
            AudioStreamInfo? audioStreamInfo = sourceHolder.AudioStream;
            VideoStreamInfo? videoStreamInfo = sourceHolder.VideoStream;
            SubtitleStreamInfo? subtitleStreamInfo = sourceHolder.SubtitleStream;

            if (videoStreamInfo is not null && chosenSource == sourceHolder.VideoSource && packet.StreamIndex == videoStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailVideoTicks);
                TimeSpan span = SpanOf(packet, lastVideoPacketSpan, videoStreamInfo.FrameRate);
                lastVideoPacketSpan = span;
                _videoPacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeVideoDecode();
            }
            else if (audioStreamInfo is not null && chosenSource == sourceHolder.AudioSource && packet.StreamIndex == audioStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailAudioTicks);
                TimeSpan span = SpanOf(packet, lastAudioPacketSpan, 0);
                lastAudioPacketSpan = span;
                _audioPacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeAudioDecode();
            }
            else if (subtitleStreamInfo is not null && chosenSource == sourceHolder.SubtitleSource && packet.StreamIndex == subtitleStreamInfo.Index)
            {
                AdvanceReaderTail(epoch, packet.Timestamp, ref _readerTailSubtitleTicks);
                TimeSpan span = SpanOf(packet, lastSubtitlePacketSpan, 0);
                lastSubtitlePacketSpan = span;
                _subtitlePacketQueue.Enqueue(new PacketEntry(packet, sourceVersions, epoch, span));
                handedOn = true;
                TryWakeSubtitleDecode();
            }

            if (!handedOn)
            {
                packet.Dispose();
            }

            await Task.Yield();
        }
    }

    private static OperationResult SeekSources(MediaSourceHolder sourceHolder, TimeSpan position)
    {
        IReadOnlyList<MediaSource> sources = sourceHolder.Sources;
        foreach (MediaSource source in sources)
        {
            if (!source.Source.CanSeek)
            {
                return OperationResult.Failure($"A source cannot seek, so the seek to {position:g} was skipped: {source.Locator}");
            }
        }

        var firstFailure = OperationResult.Success();

        foreach (MediaSource entry in sources)
        {
            OperationResult sought;
            try
            {
                sought = entry.Source.Seek(position, ChooseSeekLanding(sourceHolder, entry, position));
            }
            catch (Exception ex)
            {
                sought = OperationResult.Failure(ex);
            }

            if (!sought.IsSuccessful)
            {
                firstFailure = sought;
            }
        }

        return firstFailure;
    }

    private static int ChooseSeekLanding(MediaSourceHolder sourceHolder, MediaSource source, TimeSpan position)
    {
        VideoStreamInfo? video = sourceHolder.VideoSource == source ? sourceHolder.VideoStream : null;
        AudioStreamInfo? audio = sourceHolder.AudioSource == source ? sourceHolder.AudioStream : null;

        if (video is null)
        {
            if (audio is null)
            {
                return -1;
            }

            return audio.Index;
        }

        if (audio is not null &&
            video.Duration > TimeSpan.Zero &&
            audio.Duration > video.Duration &&
            position > video.Duration)
        {
            return audio.Index;
        }

        return video.Index;
    }

    private bool IsBuffered(MediaSourceHolder holder, TimeSpan minimumReadAhead)
    {
        TimeSpan ReadAhead(ref long ticks, TimeSpan position)
        {
            long tail = Interlocked.Read(ref ticks);
            if (tail < 0)
            {
                return TimeSpan.MinValue;
            }

            long ahead = tail - position.Ticks;
            return ahead > 0 ? TimeSpan.FromTicks(ahead) : TimeSpan.Zero;
        }

        TimeSpan position = _clock.Position;

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && ReadAhead(ref _readerTailVideoTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.AudioStream is not null && _audioDecoder.HasComponent && ReadAhead(ref _readerTailAudioTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.SubtitleStream is not null && _subtitleDecoder.HasComponent && ReadAhead(ref _readerTailSubtitleTicks, position) >= ReadAheadMax)
        {
            return true;
        }

        if (holder.VideoStream is not null && _videoDecoder.HasComponent && ReadAhead(ref _readerTailVideoTicks, position) < minimumReadAhead)
        {
            return false;
        }

        if (holder.AudioStream is not null && _audioDecoder.HasComponent && ReadAhead(ref _readerTailAudioTicks, position) < minimumReadAhead)
        {
            return false;
        }

        if (holder.SubtitleStream is not null && _subtitleDecoder.HasComponent && ReadAhead(ref _readerTailSubtitleTicks, position) < minimumReadAhead)
        {
            return false;
        }

        return true;
    }

    private void AdvanceReaderTail(long epoch, TimeSpan timestamp, ref long oldTicks)
    {
        if (Volatile.Read(ref _readerTailEpoch) != epoch)
        {
            return;
        }

        long ticks = timestamp.Ticks;

        if (ticks > Interlocked.Read(ref oldTicks))
        {
            Interlocked.Exchange(ref oldTicks, ticks);
        }
    }

    private bool IsStreamSpent(long epoch, TimeSpan position, ref long ticks)
    {
        if (Volatile.Read(ref _readerTailEpoch) != epoch)
        {
            return false;
        }

        long ticksLocal = Interlocked.Read(ref ticks);
        if (ticksLocal < 0 || ticksLocal >= position.Ticks)
        {
            return false;
        }

        long maxTicks = Math.Max(Interlocked.Read(ref _readerTailVideoTicks), Interlocked.Read(ref _readerTailAudioTicks));
        return maxTicks >= position.Ticks;
    }

    private sealed class SourceTail
    {
        public TimeSpan Tail { get; set; }

        public bool Ended { get; set; }
    }

    #endregion
}
