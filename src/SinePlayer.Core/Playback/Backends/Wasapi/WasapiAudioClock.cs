// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

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
/// </summary>
internal sealed class WasapiAudioClock : IPlaybackClock
{
    private readonly IAudioClient _client;
    private readonly IAudioClock _audioClock;
    private readonly ulong _frequency;
    private readonly object _lock = new();

    private TimeSpan _base;
    private ulong _basePosition;
    private double _rate = 1.0;

    internal WasapiAudioClock(IAudioClient client, IAudioClock audioClock, ulong frequency)
    {
        _client = client;
        _audioClock = audioClock;
        _frequency = frequency > 0 ? frequency : 1;
    }

    public TimeSpan Position
    {
        get
        {
            unsafe
            {
                _audioClock.GetPosition(out ulong position);

                // Whoever retimes the clock may be a different thread from whoever reads it now — the
                // pipeline seeks on one and the threads reading the clock are others — so the anchor is
                // taken as a whole rather than a field at a time.
                lock (_lock)
                {
                    ulong travelled = position > _basePosition ? position - _basePosition : 0;

                    // At a rate above one, more media passes per device second, and the other way round.
                    double seconds = travelled / (double)_frequency * _rate;
                    return _base + TimeSpan.FromSeconds(seconds);
                }
            }
        }
    }

    public bool IsRunning { get; private set; }

    public double Rate => _rate;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _client.Start();
        IsRunning = true;
    }

    public void Pause()
    {
        if (!IsRunning)
        {
            return;
        }

        _client.Stop();
        IsRunning = false;
    }

    public void SetPosition(TimeSpan position)
    {
        // Called after the pipeline has flushed, so the device is back at its start.
        lock (_lock)
        {
            _base = position;
            _basePosition = 0;
        }
    }

    /// <summary>
    /// Applies a new rate. The sink does not change the device's own rate yet, so only the reading of
    /// the device's time changes; the position is re-anchored so it does not jump.
    /// </summary>
    public void SetRate(double rate)
    {
        TimeSpan position = Position;
        unsafe
        {
            lock (_lock)
            {
                _audioClock.GetPosition(out _basePosition);
                _base = position;
                _rate = rate > 0 ? rate : 1.0;
            }
        }
    }
}
