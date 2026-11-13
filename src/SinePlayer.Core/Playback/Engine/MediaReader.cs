// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class MediaReader(
    PipelineTimeline timeline,
    PipelineEvents events,
    Func<MediaSource, TimeSpan, SeekMode, int> chooseSeekLanding) : IPipelineWakeTarget
{
    private const int BufferedWaitMilliseconds = 200;

    private static readonly TimeSpan ReadAheadMin = TimeSpan.FromMilliseconds(8000);
    private static readonly TimeSpan ReadAheadMax = TimeSpan.FromMilliseconds(10000);
    private static readonly TimeSpan ReadAheadWake = TimeSpan.FromMilliseconds(4000);

    private readonly PipelineNodeAsync _node = new();
    private int _resyncRequested;

    private readonly AtomicCell<IReadOnlyDictionary<IChainFeed, ComponentSlot<MediaSource>>> _videoSources = new(new Dictionary<IChainFeed, ComponentSlot<MediaSource>>());
    private readonly AtomicCell<IReadOnlyDictionary<IChainFeed, ComponentSlot<MediaSource>>> _audioSources = new(new Dictionary<IChainFeed, ComponentSlot<MediaSource>>());
    private readonly AtomicCell<IReadOnlyDictionary<IChainFeed, ComponentSlot<MediaSource>>> _subtitleSources = new(new Dictionary<IChainFeed, ComponentSlot<MediaSource>>());

    public void AddVideoSource(IChainFeed feed, ComponentSlot<MediaSource> slot)
    {
        _videoSources.Write(current => new Dictionary<IChainFeed, ComponentSlot<MediaSource>>(current) { [feed] = slot });
    }

    public void AddAudioSource(IChainFeed feed, ComponentSlot<MediaSource> slot)
    {
        _audioSources.Write(current => new Dictionary<IChainFeed, ComponentSlot<MediaSource>>(current) { [feed] = slot });
    }

    public void AddSubtitleSource(IChainFeed feed, ComponentSlot<MediaSource> slot)
    {
        _subtitleSources.Write(current => new Dictionary<IChainFeed, ComponentSlot<MediaSource>>(current) { [feed] = slot });
    }

    public void RemoveSource(IChainFeed feed)
    {
        _videoSources.Write(current => Without(current, feed));
        _audioSources.Write(current => Without(current, feed));
        _subtitleSources.Write(current => Without(current, feed));
    }

    public void RequestResync()
    {
        Interlocked.Exchange(ref _resyncRequested, 1);
        _node.Wake();
    }

    public void Wake()
    {
        _node.Wake();
    }

    public async Task Run(Func<bool> stopRequested, Action<long> reportSourceEnded)
    {
        bool pendingEpochChange = false;
        long workingEpoch = -1;
        long endingEpoch = -1;
        Dictionary<MediaSource, SourceTail> tails = [];
        List<MediaSource> sources = [];

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

            // Install sources
            IReadOnlyList<KeyValuePair<IChainFeed, ComponentSlot<MediaSource>>> bindings = ReadSources();
            bool installed = false;
            foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
            {
                if (entry.Value.Update(out _))
                {
                    installed = true;
                }
            }

            if (installed)
            {
                pendingEpochChange = true;
                tails.Clear();
                sources.Clear();

                foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
                {
                    if (entry.Value.CurrentComponent is { } source && !sources.Contains(source))
                    {
                        sources.Add(source);
                    }
                }
            }

            if (sources.Count == 0)
            {
                await _node.WaitAsync(stamp);
                continue;
            }

            if (Interlocked.Exchange(ref _resyncRequested, 0) == 1)
            {
                Dictionary<IChainFeed, TimeSpan> offsets = [];
                foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
                {
                    if (entry.Value.CurrentStream is not null)
                    {
                        offsets[entry.Key] = entry.Key.Offset;
                    }
                }

                timeline.PublishOffsets(offsets);
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

                foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
                {
                    entry.Key.ReadTail.Reset();
                }

                OperationResult sought = SeekSources(sources, epochGate.SeekPosition + epochGate.ReadOffset, epochGate.SeekMode);
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
            MediaSource? chosenSource = null;
            SourceTail? chosenTail = null;
            bool anyUnended = false;
            foreach (MediaSource candidate in sources)
            {
                if (!tails.TryGetValue(candidate, out SourceTail? tail))
                {
                    tail = new SourceTail { Tail = epochGate.SeekPosition + epochGate.ReadOffset };
                    tails[candidate] = tail;
                }

                if (tail.Ended)
                {
                    continue;
                }

                anyUnended = true;

                tail.IsBuffered = IsBuffered(candidate, tail, bindings);
                if (tail.IsBuffered)
                {
                    continue;
                }

                if (chosenTail is null || tail.Tail < chosenTail.Tail)
                {
                    chosenSource = candidate;
                    chosenTail = tail;
                }
            }

            if (chosenSource is null || chosenTail is null)
            {
                if (anyUnended)
                {
                    await _node.WaitAsync(stamp, BufferedWaitMilliseconds);
                    continue;
                }

                endingEpoch = epoch;

                foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
                {
                    entry.Key.SignalEnd(entry.Value.CurrentVersions, epoch);
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

            foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
            {
                IChainFeed chain = entry.Key;
                ComponentSlot<MediaSource> slot = entry.Value;

                if (slot.CurrentComponent != chosenSource || slot.CurrentStream?.Index != packet.StreamIndex)
                {
                    continue;
                }

                packet.Ref();
                chain.Accept(slot.CurrentStream, packet, slot.CurrentVersions, epoch);
                chain.ReadTail.Report(epoch, packet.Timestamp + epochGate.OffsetOf(chain));
            }

            packet.Dispose();
            await Task.Yield();
        }
    }

    private OperationResult SeekSources(IReadOnlyList<MediaSource> sources, TimeSpan position, SeekMode mode)
    {
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
                sought = entry.Source.Seek(position, chooseSeekLanding(entry, position, mode), mode);
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

    private bool IsBuffered(
        MediaSource source,
        SourceTail tail,
        IReadOnlyList<KeyValuePair<IChainFeed, ComponentSlot<MediaSource>>> bindings)
    {
        TimeSpan position = timeline.Position;
        TimeSpan furthest = TimeSpan.MinValue;
        TimeSpan closest = TimeSpan.MaxValue;
        TimeSpan mostDelayed = TimeSpan.MinValue;
        TimeSpan leastDelayed = TimeSpan.MaxValue;

        foreach (KeyValuePair<IChainFeed, ComponentSlot<MediaSource>> entry in bindings)
        {
            if (entry.Value.CurrentStream is null || !entry.Key.IsConsuming || entry.Value.CurrentComponent != source)
            {
                continue;
            }

            TimeSpan offset = entry.Key.Offset;
            TimeSpan readAhead = tail.Tail - (position - offset);
            furthest = TimeSpan.FromTicks(Math.Max(furthest.Ticks, readAhead.Ticks));
            closest = TimeSpan.FromTicks(Math.Min(closest.Ticks, readAhead.Ticks));
            mostDelayed = TimeSpan.FromTicks(Math.Max(mostDelayed.Ticks, offset.Ticks));
            leastDelayed = TimeSpan.FromTicks(Math.Min(leastDelayed.Ticks, offset.Ticks));
        }

        if (closest == TimeSpan.MaxValue)
        {
            return true;
        }

        TimeSpan enough = tail.IsBuffered ? ReadAheadWake : ReadAheadMin;
        return furthest >= ReadAheadMax + (mostDelayed - leastDelayed) || closest >= enough;
    }

    private static IReadOnlyDictionary<IChainFeed, ComponentSlot<MediaSource>> Without(
        IReadOnlyDictionary<IChainFeed, ComponentSlot<MediaSource>> current,
        IChainFeed feed)
    {
        if (!current.ContainsKey(feed))
        {
            return current;
        }

        List<KeyValuePair<IChainFeed, ComponentSlot<MediaSource>>> kept = [.. current.Where(entry => entry.Key != feed)];
        return new Dictionary<IChainFeed, ComponentSlot<MediaSource>>(kept);
    }

    private IReadOnlyList<KeyValuePair<IChainFeed, ComponentSlot<MediaSource>>> ReadSources()
    {
        List<KeyValuePair<IChainFeed, ComponentSlot<MediaSource>>> entries = [.. _videoSources.Read(), .. _audioSources.Read(), .. _subtitleSources.Read()];
        return entries;
    }

    private sealed class SourceTail
    {
        public TimeSpan Tail { get; set; }

        public bool Ended { get; set; }

        public bool IsBuffered { get; set; }
    }
}
