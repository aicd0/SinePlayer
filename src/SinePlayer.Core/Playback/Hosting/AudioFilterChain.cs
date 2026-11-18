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

    private static bool IsSameShape(AudioStreamInfo? first, AudioStreamInfo? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.SampleRate == second.SampleRate
            && first.ChannelCount == second.ChannelCount
            && first.ChannelLayout == second.ChannelLayout;
    }

    private readonly Lock _lock = new();
    private IAudioFilter[] _filters = [];
    private IAudioFilter[] _preForced = [];
    private IAudioFilter[] _postForced = [];
    private AudioStreamInfo? _stream;
    private AudioStreamInfo? _target;
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
                return (List<IAudioFilter>)[.. _filters];
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
            Reprepare();
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
            Reprepare();
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _filters = [];
            Reprepare();
        }
    }

    internal OperationResult Configure(AudioStreamInfo stream, AudioStreamInfo target)
    {
        lock (_lock)
        {
            if (IsSameShape(_stream, stream) && IsSameFormat(_target, target))
            {
                return OperationResult.Success();
            }

            _stream = stream;
            _target = target;
            return PrepareRunning();
        }
    }

    internal void SetPreForcedFilters(params IAudioFilter[] filters)
    {
        lock (_lock)
        {
            _preForced = [.. filters];
            Reprepare();
        }
    }

    internal void SetPostForcedFilters(params IAudioFilter[] filters)
    {
        lock (_lock)
        {
            _postForced = [.. filters];
            Reprepare();
        }
    }

    private void Reprepare()
    {
        OperationResult prepared = PrepareRunning();
        if (!prepared.IsSuccessful)
        {
            PlaybackLogger.W(TAG, $"The audio chain was left as it was: {prepared.Message}");
        }
    }

    private OperationResult PrepareRunning()
    {
        AudioStreamInfo? stream = _stream;
        AudioStreamInfo? target = _target;
        if (stream is null || target is null)
        {
            return OperationResult.Success();
        }

        AudioStreamInfo output = stream;
        List<IAudioFilter> running = [];
        OperationResult prepared = Collect(_preForced, running, ref output, target, required: true);
        if (prepared.IsSuccessful)
        {
            prepared = Collect(_filters, running, ref output, target, required: false);
        }

        if (prepared.IsSuccessful)
        {
            prepared = Collect(_postForced, running, ref output, target, required: true);
        }

        if (!prepared.IsSuccessful)
        {
            // Nothing is published: the chain keeps answering with what was last whole rather than with a working
            // set that is missing a stage the pipeline depends on.
            return prepared;
        }

        Publish([.. running], output);
        return OperationResult.Success();
    }

    private void Publish(IAudioFilter[] filters, AudioStreamInfo? format)
    {
        _snapshot = new AudioFilterChainSnapshot(filters, format, _snapshot.Version + 1);
    }

    private static OperationResult Collect(IAudioFilter[] filters, List<IAudioFilter> running, ref AudioStreamInfo output, AudioStreamInfo target, bool required)
    {
        foreach (IAudioFilter filter in filters)
        {
            OperationResult configured = TryConfigure(filter, output, target);
            if (!configured.IsSuccessful)
            {
                if (required)
                {
                    return OperationResult.Failure($"{filter.Name} could not be prepared for the audio chain: {configured.Message}", configured.Exception);
                }

                PlaybackLogger.W(TAG, $"{filter.Name} was left out of the audio chain: {configured.Message}");
                continue;
            }

            running.Add(filter);
            if (filter.OutputFormat is AudioStreamInfo produced)
            {
                output = produced;
            }
        }

        return OperationResult.Success();
    }

    private static OperationResult TryConfigure(IAudioFilter filter, AudioStreamInfo input, AudioStreamInfo target)
    {
        try
        {
            return filter.Configure(input, target);
        }
        catch (Exception exception)
        {
            return OperationResult.Failure(exception);
        }
    }
}
