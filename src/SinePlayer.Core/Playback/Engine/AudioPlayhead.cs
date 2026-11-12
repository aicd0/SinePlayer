// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

internal sealed class AudioPlayhead
{
    private static readonly TimeSpan OffsetNoise = TimeSpan.FromMilliseconds(50);

    private const double OffsetWeight = 1.0 / 16.0;

    private readonly List<Span> _spans = [];
    private long _frames;
    private bool _deviceOriginKnown;
    private TimeSpan _deviceOrigin;
    private int _sampleRate;
    private bool _offsetKnown;
    private TimeSpan _offset;

    internal void Reset()
    {
        _spans.Clear();
        _frames = 0;
        _deviceOriginKnown = false;
        _offsetKnown = false;
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

        if (!TryResolve(FramesOf(deviceTimeNotNull - _deviceOrigin, _sampleRate), out TimeSpan mediaTime))
        {
            return;
        }

        TimeSpan offset = deviceTimeNotNull - mediaTime;
        if (!_offsetKnown || (offset - _offset).Duration() > OffsetNoise)
        {
            _offset = offset;
        }
        else
        {
            _offset += (offset - _offset) * OffsetWeight;
        }

        _offsetKnown = true;
    }

    internal bool TryGetOffset(out TimeSpan offset)
    {
        offset = _offset;
        return _offsetKnown;
    }

    private bool TryResolve(long deviceFrame, out TimeSpan mediaTime)
    {
        mediaTime = default;

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
        long behindFrames = deviceFrame - span.StartFrame;
        if (behindFrames < 0)
        {
            behindFrames = 0;
        }

        long spanTicks = (span.EndTime - span.StartTime).Ticks;
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
