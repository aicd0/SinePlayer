// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Timing;

using Windows.Win32.Media.Audio;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// The clock of a WASAPI render stream. The device knows how much audio has actually reached the
/// speakers, so its position is the truth that video is matched against — including when the engine is
/// converting the sample rate, because the engine's timeline is exactly what is being heard.
/// <para>
/// The device position never moves backwards except when the stream is reset, so the position is a base
/// plus the distance the device has travelled since that base was taken. That also makes
/// <see cref="Start"/> and <see cref="Pause"/> free: a stopped stream simply stops advancing.
/// </para>
/// <para>
/// A device can be invalidated at any moment — unplugged, disabled, pre-empted, or re-formatted — and every
/// call to it then fails. That is the one kind of failure this class answers itself: the clock cannot throw at
/// whoever reads it (the host's position timer reads it, and so does every frame the picture judges), so the
/// failure is latched, the clock reads as the position it was last put at, and the *original exception* is kept
/// for the renderer to report on its next hand-off or flush — which is what turns it into a pipeline error (§9)
/// rather than a dead process. Nothing is re-worded: this class records that a call failed and by what, never
/// why it thinks the call failed.
/// </para>
/// </summary>
internal sealed class WasapiAudioClock : IPlaybackClock
{
    private readonly IWasapiClockDevice _device;
    private readonly ulong _frequency;
    private readonly object _lock = new();

    private TimeSpan _base;
    private TimeSpan _lastPosition;
    private ulong _basePosition;
    private double _rate = 1.0;
    private volatile bool _deviceLost;
    private Exception? _cause;

    /// <summary>
    /// A clock over the device it wraps, or over a stand-in whose failures are what a test is after.
    /// </summary>
    internal WasapiAudioClock(IWasapiClockDevice device, ulong frequency)
    {
        _device = device;
        _frequency = frequency > 0 ? frequency : 1;
    }

    internal WasapiAudioClock(IAudioClient client, IAudioClock audioClock, ulong frequency)
        : this(new DeviceClock(client, audioClock), frequency)
    {
    }

    /// <summary>
    /// Whether a device call has failed. A lost clock reads as the position it was last put at: time has
    /// stopped because the device has.
    /// </summary>
    public bool DeviceLost => _deviceLost;

    /// <summary>
    /// The first failure, exactly as it was thrown — the exception type, its message and its stack are the
    /// diagnosis, and this class is in no position to improve on them. Later failures do not replace it: the
    /// first one is the origin, and everything after it is a consequence.
    /// </summary>
    internal Exception? Cause => _cause;

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

                // At a rate above one, more media passes per device second, and the other way round.
                double seconds = travelled / (double)_frequency * _rate;
                _lastPosition = _base + TimeSpan.FromSeconds(seconds);
                return _lastPosition;
            }
        }
    }

    public bool IsRunning { get; private set; }

    public double Rate => _rate;

    public void Start()
    {
        if (IsRunning || _deviceLost)
        {
            return;
        }

        try
        {
            _device.Start();
            IsRunning = true;
        }
        catch (Exception exception)
        {
            MarkLost(exception);
        }
    }

    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        // A stream whose stop failed is not known to be stopped, so `IsRunning` is left alone: what is known is
        // that the device is gone, and that is the fact the renderer reports.
        try
        {
            _device.Stop();
            IsRunning = false;
        }
        catch (Exception exception)
        {
            MarkLost(exception);
        }
    }

    /// <summary>
    /// Points the clock at a new position, from wherever the device is now. The device's own position is
    /// taken as the zero point rather than assumed to be one: whoever asks for this cannot know whether the
    /// device has been reset, because the reset belongs to the thread that owns it — a seek is asked for on
    /// one thread and taken up on another, and the clock is read in between.
    /// <para>
    /// That is also what makes this honest while a device still holds audio for the position playback has
    /// just left: time reads as the position that was asked for, instead of the position plus everything the
    /// device has played since it was last reset.
    /// </para>
    /// </summary>
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
    /// Applies a new rate. The sink does not change the device's own rate yet, so only the reading of
    /// the device's time changes; the position is re-anchored so it does not jump.
    /// </summary>
    public void SetRate(double rate)
    {
        lock (_lock)
        {
            if (!_deviceLost)
            {
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

            _base = _lastPosition;
            _rate = rate > 0 ? rate : 1.0;
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
        _cause ??= exception;
        _deviceLost = true;
    }

    /// <summary>The real device, in the three calls the clock makes of it.</summary>
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
