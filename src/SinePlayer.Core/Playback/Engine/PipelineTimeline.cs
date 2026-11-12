// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Engine;

internal sealed class PipelineTimeline(Action clockMoved, Func<bool> starved)
{
    private const int ClockSyncToleranceMilliseconds = 10;

    private readonly AtomicCell<EpochGate> _epochGate = new(new());
    private readonly AtomicCell<AudioChain?> _deviceOwner = new(null);
    private readonly Lock _clockLock = new();
    private readonly RetimedClock _clock = new(new StopwatchClock());

    private long _endingEpoch = -1;
    private long _anchoredEpoch = -1;
    private TimeSpan? _pendingPosition;

    public Lock ClockLock => _clockLock;

    public AudioChain? DeviceOwner
    {
        get => _deviceOwner.Read();
        set => _deviceOwner.Write(_ => value);
    }

    public EpochGate Gate => _epochGate.Read();

    public long Epoch => _epochGate.Read().Epoch;

    public TimeSpan Position => _clock.Position;

    public bool IsRunning => _clock.IsRunning;

    public double Rate => _clock.Rate;

    public long EndingEpoch
    {
        get => Interlocked.Read(ref _endingEpoch);
        set => Interlocked.Exchange(ref _endingEpoch, value);
    }

    public void SeekTo(TimeSpan position, SeekMode mode)
    {
        lock (_clockLock)
        {
            _pendingPosition = position;
            _epochGate.Write(x => new EpochGate()
            {
                Epoch = x.Epoch + 1,
                SeekPosition = position,
                SeekMode = mode,
                ReadOffset = x.ReadOffset,
                Offsets = x.Offsets,
            });
            _clock.SetPosition(position);
        }
    }

    public void Refresh()
    {
        lock (_clockLock)
        {
            TimeSpan position = _pendingPosition ?? _clock.Position;
            _epochGate.Write(x => new EpochGate()
            {
                Epoch = x.Epoch + 1,
                SeekPosition = position,
                SeekMode = SeekMode.Precise,
                ReadOffset = x.ReadOffset,
                Offsets = x.Offsets,
            });
        }
    }

    public void PublishOffsets(IReadOnlyDictionary<IChainFeed, TimeSpan> offsets)
    {
        TimeSpan readOffset = EpochGate.ReadOffsetOf(offsets);
        lock (_clockLock)
        {
            TimeSpan position = _pendingPosition ?? _clock.Position;
            _epochGate.Write(x =>
            {
                bool reseek = readOffset != x.ReadOffset || EpochGate.AnyOffsetIncreased(x.Offsets, offsets);
                return new EpochGate()
                {
                    Epoch = reseek ? x.Epoch + 1 : x.Epoch,
                    SeekPosition = reseek ? position : x.SeekPosition,
                    SeekMode = reseek ? SeekMode.Precise : x.SeekMode,
                    ReadOffset = readOffset,
                    Offsets = offsets,
                };
            });
        }
    }

    public void SetRate(double rate)
    {
        lock (_clockLock)
        {
            _clock.SetRate(rate);
        }
    }

    public void Stop()
    {
        lock (_clockLock)
        {
            _clock.Pause();
            _anchoredEpoch = -1;
        }
    }

    public void StartOrPause(long epoch, MediaEngineState state)
    {
        lock (_clockLock)
        {
            bool start = state == MediaEngineState.Playing && !starved();
            if (start == _clock.IsRunning)
            {
                return;
            }

            if (start && _anchoredEpoch != epoch)
            {
                return;
            }

            if (start)
            {
                _clock.Start();
            }
            else
            {
                _clock.Pause();
            }
        }
    }

    public bool Anchor(long iterationEpoch, TimeSpan mediaTimestamp)
    {
        lock (_clockLock)
        {
            if (_anchoredEpoch == iterationEpoch || iterationEpoch != Epoch)
            {
                return false;
            }

            _anchoredEpoch = iterationEpoch;
            _pendingPosition = null;
            _clock.SetPosition(mediaTimestamp);
            clockMoved();
            return true;
        }
    }

    public void SyncClock(AudioChain chain, TimeSpan position)
    {
        if (chain != DeviceOwner)
        {
            return;
        }

        TimeSpan localTime = _clock.Position;
        if ((position - localTime).Duration() <= TimeSpan.FromMilliseconds(ClockSyncToleranceMilliseconds * Rate))
        {
            return;
        }

        lock (_clockLock)
        {
            if (!_clock.IsRunning || _anchoredEpoch != Epoch)
            {
                return;
            }

            _clock.SetPosition(position);
            clockMoved();
        }
    }

    public int ToIdleTime(TimeSpan remainingTime)
    {
        double rate = Rate;
        double milliseconds = remainingTime.TotalMilliseconds / (rate > 0 ? rate : 1.0);
        return (int)Math.Clamp(Math.Ceiling(milliseconds), 1, int.MaxValue);
    }
}
