// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Hosting;

public sealed class AudioFilterChain
{
    private const string TAG = nameof(AudioFilterChain);

    internal static bool IsSameFormat(AudioStreamInfo? first, AudioStreamInfo? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.SampleRate == second.SampleRate
            && first.ChannelCount == second.ChannelCount
            && first.ChannelLayout == second.ChannelLayout
            && first.SampleFormat == second.SampleFormat;
    }

    private readonly Lock _lock = new();
    private IAudioFilter[] _filters = [];
    private IAudioFilter[] _forced = [];
    private AudioStreamInfo? _inputFormat;
    private AudioStreamInfo? _targetFormat;
    private volatile AudioFilterChainSnapshot _snapshot = AudioFilterChainSnapshot.Empty;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _filters.Length;
            }
        }
    }

    public IReadOnlyList<IAudioFilter> Filters
    {
        get
        {
            lock (_lock)
            {
                return _filters.ToArray();
            }
        }
    }

    internal long Version => _snapshot.Version;

    internal AudioFilterChainSnapshot Snapshot() => _snapshot;

    public void Add(IAudioFilter filter)
    {
        lock (_lock)
        {
            if (Array.IndexOf(_filters, filter) >= 0)
            {
                throw new InvalidOperationException("That filter is already in the chain.");
            }

            _filters = [.. _filters, filter];
            PrepareRunning();
        }
    }

    public bool Remove(IAudioFilter filter)
    {
        lock (_lock)
        {
            int index = Array.IndexOf(_filters, filter);
            if (index < 0)
            {
                return false;
            }

            _filters = [.. _filters[..index], .. _filters[(index + 1)..]];
            PrepareRunning();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _filters = [];
            PrepareRunning();
        }
    }

    internal void Configure(AudioStreamInfo stream, AudioStreamInfo target)
    {
        lock (_lock)
        {
            if (IsSameFormat(_inputFormat, stream) && IsSameFormat(_targetFormat, target))
            {
                return;
            }

            _inputFormat = stream;
            _targetFormat = target;
            PrepareRunning();
        }
    }

    internal void SetForcedFilters(params IAudioFilter[] filters)
    {
        lock (_lock)
        {
            _forced = [.. filters];
            PrepareRunning();
        }
    }

    private void PrepareRunning()
    {
        AudioStreamInfo? stream = _inputFormat;
        AudioStreamInfo? target = _targetFormat;
        if (stream is null || target is null)
        {
            return;
        }

        AudioStreamInfo output = stream;
        List<IAudioFilter> running = [];
        Collect(_filters, running, ref output, target);
        Collect(_forced, running, ref output, target);
        Publish([.. running], output);
    }

    private void Publish(IAudioFilter[] filters, AudioStreamInfo? format)
    {
        _snapshot = new AudioFilterChainSnapshot(filters, format, _snapshot.Version + 1);
    }

    private static void Collect(IAudioFilter[] filters, List<IAudioFilter> running, ref AudioStreamInfo output, AudioStreamInfo target)
    {
        foreach (IAudioFilter filter in filters)
        {
            if (!TryConfigure(filter, output, target))
            {
                continue;
            }

            running.Add(filter);
            if (filter.OutputFormat is AudioStreamInfo produced)
            {
                output = produced;
            }
        }
    }

    private static bool TryConfigure(IAudioFilter filter, AudioStreamInfo input, AudioStreamInfo target)
    {
        try
        {
            OperationResult configured = filter.Configure(input, target);
            if (configured.IsSuccessful)
            {
                return true;
            }

            PlaybackLogger.W(TAG, $"{filter.Name} was left out of the audio chain: {configured.Message}");
            return false;
        }
        catch (Exception exception)
        {
            PlaybackLogger.W(TAG, $"{filter.Name} was left out of the audio chain: {exception.Message}");
            return false;
        }
    }
}
