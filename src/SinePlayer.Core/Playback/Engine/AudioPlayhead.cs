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

    internal void OnHandOver(TimeSpan mediaStart, int frames, int sampleRate, TimeSpan? devicePosition)
    {
        Append(mediaStart, frames, sampleRate);

        if (devicePosition is not TimeSpan device || sampleRate <= 0)
        {
            return;
        }

        if (!_deviceOriginKnown)
        {
            _deviceOrigin = device;
            _sampleRate = sampleRate;
            _deviceOriginKnown = true;
        }

        if (!TryResolve(FramesOf(device - _deviceOrigin, _sampleRate), out TimeSpan position))
        {
            return;
        }

        TimeSpan sample = device - position;
        if (!_offsetKnown || (sample - _offset).Duration() > OffsetNoise)
        {
            _offset = sample;
        }
        else
        {
            _offset += (sample - _offset) * OffsetWeight;
        }

        _offsetKnown = true;
    }

    internal bool TryGetOffset(out TimeSpan offset)
    {
        offset = _offset;
        return _offsetKnown;
    }

    private bool TryResolve(long played, out TimeSpan mediaTime)
    {
        mediaTime = default;

        if (played < 0 || played >= _frames)
        {
            return false;
        }

        while (_spans.Count > 0 && _spans[0].End <= played)
        {
            _spans.RemoveAt(0);
        }

        if (_spans.Count == 0)
        {
            return false;
        }

        Span span = _spans[0];
        mediaTime = span.MediaStart + TimeOf(played - span.Start, span.SampleRate);
        return true;
    }

    private static long FramesOf(TimeSpan elapsed, int sampleRate)
    {
        return elapsed.Ticks * sampleRate / TimeSpan.TicksPerSecond;
    }

    private static TimeSpan TimeOf(long frames, int sampleRate)
    {
        return TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / sampleRate);
    }

    private void Append(TimeSpan mediaStart, int frames, int sampleRate)
    {
        if (frames <= 0 || sampleRate <= 0)
        {
            return;
        }

        _spans.Add(new Span(_frames, _frames + frames, mediaStart, sampleRate));
        _frames += frames;
    }

    private readonly record struct Span(long Start, long End, TimeSpan MediaStart, int SampleRate);
}
