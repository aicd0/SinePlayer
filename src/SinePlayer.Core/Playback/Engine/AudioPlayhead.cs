// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

internal sealed class AudioPlayhead
{
    private readonly List<Span> _spans = [];
    private long _frames;
    private bool _deviceOriginKnown;
    private TimeSpan _deviceOrigin;
    private int _sampleRate;

    internal void Reset()
    {
        _spans.Clear();
        _frames = 0;
        _deviceOriginKnown = false;
    }

    internal void OnHandOver(int frames, int sampleRate, TimeSpan startTime, TimeSpan endTime, TimeSpan? deviceTime)
    {
        Append(frames, sampleRate, startTime, endTime);

        if (deviceTime is not TimeSpan deviceTimeNotNull || sampleRate <= 0)
        {
            return;
        }

        if (!_deviceOriginKnown)
        {
            _deviceOrigin = deviceTimeNotNull;
            _sampleRate = sampleRate;
            _deviceOriginKnown = true;
        }
    }

    internal bool TryResolve(TimeSpan deviceTime, out TimeSpan mediaTime)
    {
        mediaTime = default;

        if (!_deviceOriginKnown)
        {
            return false;
        }

        long deviceFrame = FramesOf(deviceTime - _deviceOrigin, _sampleRate);
        if (deviceFrame < 0 || deviceFrame >= _frames)
        {
            return false;
        }

        while (_spans.Count > 0 && _spans[0].EndFrame <= deviceFrame)
        {
            _spans.RemoveAt(0);
        }

        if (_spans.Count == 0)
        {
            return false;
        }

        Span span = _spans[0];
        long spanFrames = span.EndFrame - span.StartFrame;
        long spanTicks = (span.EndTime - span.StartTime).Ticks;
        long behindFrames = Math.Max(0, deviceFrame - span.StartFrame);

        mediaTime = span.StartTime + TimeSpan.FromTicks(behindFrames * spanTicks / spanFrames);
        return true;
    }

    private void Append(int frames, int sampleRate, TimeSpan startTime, TimeSpan endTime)
    {
        if (frames <= 0 || sampleRate <= 0)
        {
            return;
        }

        _spans.Add(new()
        {
            StartFrame = _frames,
            EndFrame = _frames + frames,
            StartTime = startTime,
            EndTime = endTime > startTime ? endTime : startTime + TimeOf(frames, sampleRate),
        });
        _frames += frames;
    }

    private static long FramesOf(TimeSpan elapsed, int sampleRate)
    {
        return elapsed.Ticks * sampleRate / TimeSpan.TicksPerSecond;
    }

    private static TimeSpan TimeOf(long frames, int sampleRate)
    {
        return TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / sampleRate);
    }

    private readonly record struct Span(long StartFrame, long EndFrame, TimeSpan StartTime, TimeSpan EndTime);
}
