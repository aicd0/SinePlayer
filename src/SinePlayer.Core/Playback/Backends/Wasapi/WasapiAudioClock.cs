// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Timing;

using Windows.Win32.Media.Audio;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

internal sealed class WasapiAudioClock : IPlaybackClock
{
    private readonly Lock _lock = new();
    private readonly IWasapiClockDevice _device;
    private readonly ulong _frequency;
    private readonly Action? _beforeStart;

    private TimeSpan _base;
    private TimeSpan _lastPosition;
    private ulong _basePosition;
    private volatile bool _deviceLost;
    private Exception? _error;

    internal WasapiAudioClock(IWasapiClockDevice device, ulong frequency, Action? beforeStart = null)
    {
        _device = device;
        _frequency = frequency > 0 ? frequency : 1;
        _beforeStart = beforeStart;
    }

    internal WasapiAudioClock(IAudioClient client, IAudioClock audioClock, ulong frequency, Action? beforeStart = null)
        : this(new DeviceClock(client, audioClock), frequency, beforeStart)
    {
    }

    public bool DeviceLost => _deviceLost;

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_deviceLost)
                {
                    return _lastPosition;
                }

                ulong position;
                try
                {
                    position = _device.Position;
                }
                catch (Exception exception)
                {
                    MarkLost(exception);
                    return _lastPosition;
                }

                ulong travelled = position > _basePosition ? position - _basePosition : 0;
                double seconds = travelled / (double)_frequency;
                _lastPosition = _base + TimeSpan.FromSeconds(seconds);
                return _lastPosition;
            }
        }
    }

    public bool IsRunning { get; private set; }

    internal Exception? Error => _error;

    public void Start()
    {
        if (IsRunning || _deviceLost)
        {
            return;
        }

        try
        {
            _beforeStart?.Invoke();
            _device.Start();
            IsRunning = true;
        }
        catch (Exception exception)
        {
            MarkLost(exception);
            throw;
        }
    }

    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            _device.Stop();
            IsRunning = false;
        }
        catch (Exception exception)
        {
            MarkLost(exception);
            throw;
        }
    }

    public void SetPosition(TimeSpan position)
    {
        lock (_lock)
        {
            _base = position;
            _lastPosition = position;

            if (_deviceLost)
            {
                // Nothing to anchor to, so the position that was placed is the answer — which is what a
                // frozen clock means.
                _basePosition = 0;
                return;
            }

            try
            {
                _basePosition = _device.Position;
            }
            catch (Exception exception)
            {
                MarkLost(exception);
                _basePosition = 0;
            }
        }
    }

    /// <summary>
    /// Says that a device call made elsewhere — the renderer's padding read, its write of a block, its reset —
    /// failed. The clock owns this fact because it is the object the renderer already holds for the device's
    /// state, so there is one answer to "is the device gone" rather than a copy per caller.
    /// </summary>
    internal void ReportDeviceLost(Exception exception)
    {
        MarkLost(exception);
    }

    /// <summary>
    /// Says that the renderer has let go of the device, so this clock must stop touching it. A reader may still
    /// hold the clock — the pipeline's corrected clock reads it on another thread until it is replaced — and it must
    /// keep answering, from the last position it knew, rather than reaching into a released client.
    /// </summary>
    internal void OnDeviceReleased()
    {
        lock (_lock)
        {
            _deviceLost = true;
        }
    }

    /// <summary>
    /// Latches the first failure. Called with the lock held, or with an exception that must be recorded as a
    /// whole: the flag is published after its cause, so whoever sees the flag can read the cause.
    /// </summary>
    private void MarkLost(Exception exception)
    {
        _error ??= exception;
        _deviceLost = true;
    }

    private sealed class DeviceClock : IWasapiClockDevice
    {
        private readonly IAudioClient _client;
        private readonly IAudioClock _audioClock;

        internal DeviceClock(IAudioClient client, IAudioClock audioClock)
        {
            _client = client;
            _audioClock = audioClock;
        }

        public ulong Position
        {
            get
            {
                unsafe
                {
                    _audioClock.GetPosition(out ulong position);
                    return position;
                }
            }
        }

        public void Start()
        {
            _client.Start();
        }

        public void Stop()
        {
            _client.Stop();
        }
    }
}
