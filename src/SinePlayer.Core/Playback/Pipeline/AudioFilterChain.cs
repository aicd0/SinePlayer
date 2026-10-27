// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

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
                return [.. _filters];
            }
        }
    }

    /// <summary>
    /// Counts the times the chain has been published — prepared, or edited — so a caller holding a
    /// <see cref="Snapshot"/> can tell, for the price of one read, whether there is a newer one to take. It says nothing
    /// about the format: a snapshot that moved on may hand on the same shape, and only the formats answer that.
    /// </summary>
    public long Version => _snapshot.Version;

    /// <summary>
    /// The chain as it is now. The answer never changes afterwards, so a caller that has asked once can keep running
    /// blocks through it while the host edits the chain underneath.
    /// </summary>
    public AudioFilterChainSnapshot Snapshot() => _snapshot;

    public void Add(IAudioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_lock)
        {
            if (Array.IndexOf(_filters, filter) >= 0)
            {
                throw new InvalidOperationException("That filter is already in the chain.");
            }

            _filters = [.. _filters, filter];

            AudioFilterChainSnapshot snapshot = _snapshot;
            if (_inputFormat is not AudioStreamInfo stream || !TryConfigure(filter, snapshot.OutputFormat ?? stream, _targetFormat ?? stream))
            {
                return;
            }

            Publish([.. snapshot.Filters, filter], filter.OutputFormat ?? snapshot.OutputFormat);
        }
    }

    public bool Remove(IAudioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_lock)
        {
            int index = Array.IndexOf(_filters, filter);
            if (index < 0)
            {
                return false;
            }

            bool wasRunning = _snapshot.Filters.Contains(filter);

            _filters = [.. _filters[..index], .. _filters[(index + 1)..]];
            if (wasRunning && _inputFormat is AudioStreamInfo stream && _targetFormat is AudioStreamInfo target)
            {
                // What is left is prepared again, because a filter that was given what the removed one handed on now
                // has something else before it.
                PrepareRunning(stream, target);
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _filters = [];
            Publish([], _inputFormat);
        }
    }

    internal void Configure(AudioStreamInfo stream, AudioStreamInfo target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        lock (_lock)
        {
            if (IsSameFormat(_inputFormat, stream) && IsSameFormat(_targetFormat, target))
            {
                return;
            }

            _inputFormat = stream;
            _targetFormat = target;
            PrepareRunning(stream, target);
        }
    }

    private void PrepareRunning(AudioStreamInfo stream, AudioStreamInfo target)
    {
        AudioStreamInfo output = stream;
        List<IAudioFilter> running = [];
        foreach (IAudioFilter filter in _filters)
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

        Publish([.. running], output);
    }

    private void Publish(IAudioFilter[] filters, AudioStreamInfo? format)
    {
        // Every publication is a new version, whether or not the shape moved: a filter that leaves the format alone
        // still changes what a block goes through. Whoever keeps a device open compares the format itself, so a version
        // that moved without it costs a comparison and no reopening.
        _snapshot = new AudioFilterChainSnapshot(filters, format, _snapshot.Version + 1);
    }

    /// <summary>
    /// Asks one filter whether it can work with the stream, writing why not when it cannot. A filter that cannot
    /// take part is a line in the log, never a reason to stop playing and never something the caller has to
    /// handle.
    /// </summary>
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
