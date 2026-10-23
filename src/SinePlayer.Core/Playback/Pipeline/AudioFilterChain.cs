// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The audio filters a host has added to a pipeline, in the order they were added, and the chain that runs them:
/// a block of sound goes through them in that order before a renderer is handed it.
/// <para>
/// The filters are the host's. This chain never disposes one — not when it is removed, not when the pipeline that
/// owned the chain is gone — so whoever made a filter still owns it, and a filter must therefore tolerate being
/// told about a level by a host that has outlived the chain.
/// </para>
/// <para>
/// The chain remembers the stream it was given, which is what makes an edit take effect at once: a filter added
/// while the stream is known is prepared for it there and then and joins the running chain, so nothing has to be
/// reconciled for the sound to change. A block never sees a half-edited chain — the running set is replaced whole
/// and the audio thread reads it once per block — and editing is therefore safe from whichever thread the host is
/// on.
/// </para>
/// <para>
/// A filter that cannot be used is the chain's own business: one that refuses the stream, throws, or fails a block
/// is left out of (or dropped from) the running chain and written to the log, and the sound carries on without
/// it. The effect is lost, the block is not.
/// </para>
/// </summary>
public sealed class AudioFilterChain
{
    private const string TAG = nameof(AudioFilterChain);

    private readonly Lock _lock = new();

    /// <summary>What the host has added, in order.</summary>
    private IAudioFilter[] _filters = [];

    /// <summary>What runs, in order: the added filters that accepted the stream and have not since failed.</summary>
    private volatile IAudioFilter[] _running = [];

    /// <summary>The stream the running chain was prepared for, or null before anything is open.</summary>
    private AudioStreamInfo? _stream;

    /// <summary>How many filters have been added.</summary>
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

    /// <summary>The filters in the order they were added, as a copy that later edits do not reach.</summary>
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
    /// Adds a filter to the end of the chain. If a stream is already open it is prepared for it here and takes part
    /// in the next block; if it cannot be prepared it is logged and left out, and adding it still stands.
    /// </summary>
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

            if (_stream is AudioStreamInfo stream && TryConfigure(filter, stream))
            {
                _running = [.. _running, filter];
            }
        }
    }

    /// <summary>
    /// Takes a filter out of the chain and hands it back as it was: it is not disposed, so a host can put it back
    /// or dispose it itself.
    /// </summary>
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

            _filters = [.. _filters[..index], .. _filters[(index + 1)..]];

            int running = Array.IndexOf(_running, filter);
            if (running >= 0)
            {
                _running = [.. _running[..running], .. _running[(running + 1)..]];
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _filters = [];
            _running = [];
        }
    }

    /// <summary>
    /// Tells the chain what is being played, preparing the added filters for it. Called by the pipeline once the
    /// stream is known; being told about the same stream twice changes nothing, so a reconcile costs nothing.
    /// </summary>
    internal void Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        lock (_lock)
        {
            if (ReferenceEquals(_stream, stream))
            {
                return;
            }

            _stream = stream;

            List<IAudioFilter> running = [];
            foreach (IAudioFilter filter in _filters)
            {
                if (TryConfigure(filter, stream))
                {
                    running.Add(filter);
                }
            }

            _running = [.. running];
        }
    }

    /// <summary>
    /// Runs a block through the chain. The block is taken over: whatever comes back is the caller's to dispose,
    /// which is the same instance when nothing transformed it and the last filter's output when something did —
    /// the blocks in between are released here, so a copy a filter makes is not a leak.
    /// </summary>
    internal RawAudioBuffer Apply(RawAudioBuffer input)
    {
        ArgumentNullException.ThrowIfNull(input);

        IAudioFilter[] running = _running;
        if (running.Length == 0)
        {
            return input;
        }

        RawAudioBuffer current = input;

        foreach (IAudioFilter filter in running)
        {
            RawAudioBuffer produced = current;
            PlaybackOperationResult applied;

            try
            {
                applied = filter.Apply(current, out produced);
            }
            catch (Exception exception)
            {
                applied = PlaybackOperationResult.Failure(exception);
            }

            if (applied.IsSuccessful)
            {
                if (!ReferenceEquals(produced, current))
                {
                    current.Dispose();
                    current = produced;
                }

                continue;
            }

            PlaybackLogger.W(TAG, $"{filter.Name} was left out of the audio chain: {applied.Message}");
            DropFromRunning(filter);
        }

        return current;
    }

    /// <summary>
    /// Asks one filter whether it can work with the stream, writing why not when it cannot. A filter that cannot
    /// take part is a line in the log, never a reason to stop playing and never something the caller has to
    /// handle.
    /// </summary>
    private static bool TryConfigure(IAudioFilter filter, AudioStreamInfo stream)
    {
        try
        {
            PlaybackOperationResult configured = filter.Configure(stream);
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

    /// <summary>
    /// Forgets a filter that failed a block. Guarded because the host edits the same lists from its own thread —
    /// the two never run at once, but they can run near each other, and one must not undo the other.
    /// </summary>
    private void DropFromRunning(IAudioFilter filter)
    {
        lock (_lock)
        {
            int index = Array.IndexOf(_running, filter);
            if (index < 0)
            {
                return;
            }

            _running = [.. _running[..index], .. _running[(index + 1)..]];
        }
    }
}
