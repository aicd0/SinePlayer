// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Timing;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineTimeline(Action clockMoved, Func<bool> starved)
{
    private const string TAG = nameof(PlaybackPipeline);

    private const int ClockSyncToleranceMilliseconds = 10;

    private readonly AtomicCell<EpochGate> _epochGate = new(new());
    private readonly AtomicCell<IChainFeed?> _deviceOwner = new(null);
    private readonly Lock _clockLock = new();
    private readonly IPlaybackClock _clock = new StopwatchClock();

    private long _endingEpoch = -1;
    private long _anchoredEpoch = -1;

    public Lock ClockLock => _clockLock;

    public IChainFeed? DeviceOwner
    {
        get => _deviceOwner.Read();
        set => _deviceOwner.Write(_ => value);
    }

    public EpochGate Gate => _epochGate.Read();

    public long Epoch => _epochGate.Read().Epoch;

    public TimeSpan Position => _clock.Position;

    public bool IsRunning => _clock.IsRunning;

    public long EndingEpoch
    {
        get => Interlocked.Read(ref _endingEpoch);
        set => Interlocked.Exchange(ref _endingEpoch, value);
    }

    public void PublishMove(TimeSpan position, SeekMode mode)
    {
        _epochGate.Write(x => new EpochGate()
        {
            Epoch = x.Epoch + 1,
            SeekPosition = position,
            SeekMode = mode,
            ReadOffset = x.ReadOffset,
            Offsets = x.Offsets,
        });
    }

    public void SetPosition(TimeSpan position)
    {
        lock (_clockLock)
        {
            _clock.SetPosition(position);
        }
    }

    // A seek moves the clock at once only while nothing has anchored it at this epoch, because an anchored clock
    // is already where the chain that owns it says the sound or the picture is.
    public void SeekTo(TimeSpan position)
    {
        lock (_clockLock)
        {
            if (_anchoredEpoch != Epoch)
            {
                _clock.SetPosition(position);
            }
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
            _clock.SetPosition(mediaTimestamp);
            clockMoved();
            return true;
        }
    }

    public void SyncClock(IChainFeed chain, IPlaybackClock deviceClock, TimeSpan offset)
    {
        if (chain != DeviceOwner)
        {
            return;
        }

        TimeSpan deviceTime;
        TimeSpan localTime;
        try
        {
            deviceTime = deviceClock.Position;
            localTime = _clock.Position;
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
            if (!_clock.IsRunning || _anchoredEpoch != Epoch)
            {
                return;
            }

            _clock.SetPosition(expected);
            clockMoved();
        }
    }
}
