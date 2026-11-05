// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class MediaReader(
    ComponentSlot<MediaSourceHolder> source,
    IReadOnlyList<IChainFeed> feeds,
    PipelineTimeline timeline,
    PipelineEvents events,
    Func<MediaSource, TimeSpan, int> chooseSeekLanding) : IPipelineWakeTarget
{
    private static readonly TimeSpan ReadAheadMin = TimeSpan.FromMilliseconds(8000);
    private static readonly TimeSpan ReadAheadMax = TimeSpan.FromMilliseconds(10000);
    private static readonly TimeSpan ReadAheadWake = TimeSpan.FromMilliseconds(4000);

    private readonly PipelineNodeAsync _node = new();

    public void Wake()
    {
        _node.Wake();
    }

    public void TryWake()
    {
        if (!IsBuffered(ReadAheadWake))
        {
            _node.Wake();
        }
    }

    public async Task Run(Func<bool> stopRequested, Action<long> reportSourceEnded)
    {
        bool pendingEpochChange = false;
        bool buffering = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        Dictionary<MediaSource, SourceTail> tails = [];

        while (true)
        {
            long stamp = _node.Stamp;
            if (stopRequested())
            {
                break;
            }

            if (!events.CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
            {
                await _node.WaitAsync(stamp);
                continue;
            }

            // Install source
            if (source.Update(out MediaSourceHolder? oldSourceHolder))
            {
                oldSourceHolder?.Dispose();
                pendingEpochChange = true;
                tails.Clear();
            }

            MediaSourceHolder? sourceHolder = source.CurrentComponent;
            if (sourceHolder is null)
            {
                await _node.WaitAsync(stamp);
                continue;
            }

            IReadOnlyList<MediaSource> sources = sourceHolder.Sources;
            if (sources.Count == 0)
            {
                await _node.WaitAsync(stamp);
                continue;
            }

            // Bump epoch
            EpochGate epochGate = timeline.Gate;
            long epoch = epochGate.Epoch;
            if (epoch != workingEpoch)
            {
                workingEpoch = epoch;
                pendingEpochChange = false;
                tails.Clear();

                foreach (IChainFeed feed in feeds)
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
                    events.PostWarning(sought.Message);
                }

                continue;
            }

            if (pendingEpochChange || endingEpoch == epoch)
            {
                await _node.WaitAsync(stamp);
                continue;
            }

            // Buffering
            if (!buffering)
            {
                if (IsBuffered(ReadAheadWake))
                {
                    await _node.WaitAsync(stamp);
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
            foreach (MediaSource candidate in sources)
            {
                if (!tails.TryGetValue(candidate, out SourceTail? tail))
                {
                    tail = new SourceTail { Tail = epochGate.SeekPosition };
                    tails[candidate] = tail;
                }

                if (tail.Ended)
                {
                    continue;
                }

                if (chosenTail is null || tail.Tail < chosenTail.Tail)
                {
                    chosenSource = candidate;
                    chosenTail = tail;
                }
            }

            PipelineVersion sourceVersions = source.CurrentVersions;
            if (chosenSource is null || chosenTail is null)
            {
                endingEpoch = epoch;

                foreach (IChainFeed feed in feeds)
                {
                    feed.SignalEnd(sourceVersions, epoch);
                }

                long endedEpoch = epoch;
                _ = events.Submit(() => reportSourceEnded(endedEpoch));
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
                events.PostFailure(read);
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

            bool handedOn = false;
            foreach (IChainFeed feed in feeds)
            {
                if (feed.TryAccept(chosenSource, packet, sourceVersions, epoch))
                {
                    feed.Tail.Report(epoch, packet.Timestamp);
                    handedOn = true;
                    break;
                }
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
                sought = entry.Source.Seek(position, chooseSeekLanding(entry, position));
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

    private bool IsBuffered(TimeSpan minimumReadAhead)
    {
        TimeSpan position = timeline.Position;

        foreach (IChainFeed feed in feeds)
        {
            if (feed.IsActive && feed.Tail.ReadAhead(position) >= ReadAheadMax)
            {
                return true;
            }
        }

        foreach (IChainFeed feed in feeds)
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
}
