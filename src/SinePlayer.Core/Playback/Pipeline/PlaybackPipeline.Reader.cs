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
        if (!IsBuffered(ReadAheadWake))
        {
            _readerNode.Wake();
        }
    }

    private async Task ReaderCore()
    {
        ComponentSlot<MediaSourceHolder> sourceSlot = _source;
        bool pendingEpochChange = false;
        bool buffering = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        Dictionary<MediaSource, SourceTail> tails = [];

        while (true)
        {
            long stamp = _readerNode.Stamp;
            if (_stopRequested)
            {
                break;
            }

            if (!_events.CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
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
            EpochGate epochGate = _timeline.Gate;
            long epoch = epochGate.Epoch;
            if (epoch != workingEpoch)
            {
                workingEpoch = epoch;
                pendingEpochChange = false;
                tails.Clear();

                foreach (IChainFeed feed in _feeds)
                {
                    feed.Tail.Reset();
                }

                OperationResult sought = SeekSources(sourceHolder, epochGate.SeekPosition);
                if (sought.IsSuccessful)
                {
                    endingEpoch = -1;
                }
                else
                {
                    _events.PostWarning(sought.Message);
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
                if (IsBuffered(ReadAheadWake))
                {
                    await _readerNode.WaitAsync(stamp);
                    continue;
                }

                buffering = true;
            }
            else if (IsBuffered(ReadAheadMin))
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

                _video.PacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _audio.PacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _subtitle.PacketQueue.Enqueue(new PacketEntry(null, sourceVersions, epoch, TimeSpan.Zero));
                _video.WakeDecode();
                _audio.WakeDecode();
                _subtitle.WakeDecode();

                long endedEpoch = epoch;
                _ = _events.Submit(() => NotifySourceEnded(endedEpoch));
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
                _events.PostFailure(read);
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

            if (_video.TryAccept(chosenSource, packet, sourceVersions, epoch))
            {
                _video.Tail.Report(epoch, packet.Timestamp);
                handedOn = true;
            }
            else if (_audio.TryAccept(chosenSource, packet, sourceVersions, epoch))
            {
                _audio.Tail.Report(epoch, packet.Timestamp);
                handedOn = true;
            }
            else if (_subtitle.TryAccept(chosenSource, packet, sourceVersions, epoch))
            {
                _subtitle.Tail.Report(epoch, packet.Timestamp);
                handedOn = true;
            }

            if (!handedOn)
            {
                packet.Dispose();
            }

            await Task.Yield();
        }
    }

    private OperationResult SeekSources(MediaSourceHolder sourceHolder, TimeSpan position)
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
                sought = entry.Source.Seek(position, ChooseSeekLanding(entry, position));
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

    private int ChooseSeekLanding(MediaSource source, TimeSpan position)
    {
        VideoStreamInfo? video = _video.Binding.Source == source ? _video.Binding.Stream : null;
        AudioStreamInfo? audio = _audio.Binding.Source == source ? _audio.Binding.Stream : null;

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

    private bool IsBuffered(TimeSpan minimumReadAhead)
    {
        TimeSpan position = _timeline.Position;

        foreach (IChainFeed feed in _feeds)
        {
            if (feed.IsActive && feed.Tail.ReadAhead(position) >= ReadAheadMax)
            {
                return true;
            }
        }

        foreach (IChainFeed feed in _feeds)
        {
            if (feed.IsActive && feed.Tail.ReadAhead(position) < minimumReadAhead)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class SourceTail
    {
        public TimeSpan Tail { get; set; }

        public bool Ended { get; set; }
    }

    #endregion
}
