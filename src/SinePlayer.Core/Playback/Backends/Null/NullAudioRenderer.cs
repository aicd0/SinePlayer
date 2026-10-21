// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// An output device that plays nothing. It takes every block it is handed and keeps none of it, so the audio
/// chain still buffers, submits, flushes and ends exactly as it would, while nothing needs an endpoint or a mixer.
/// <para>
/// Its clock is a stopwatch: the pipeline starts and pauses it like a device's, and the position advances with
/// time rather than with the audio. That is the one thing this backend cannot imitate — a real device's clock is
/// its own playback progress — so it behaves like a pipeline with no sound at all, which is what makes it usable
/// where no output device can be had.
/// </para>
/// <para>
/// <see cref="SubmittedSampleCount"/> is what a test watches to see that a switch happened: the new instance
/// starts from zero and the replaced one stops counting.
/// </para>
/// </summary>
public sealed class NullAudioRenderer : IAudioRenderer
{
    private readonly IPlaybackClock _clock = new StopwatchClock();

    private long _submittedSampleCount;

    public string Name => "null";

    /// <summary>A stopwatch, because there is no playback for a device clock to describe.</summary>
    public IPlaybackClock Clock => _clock;

    /// <summary>Nothing: what it is given it is done with immediately.</summary>
    public long QueuedSampleCount => 0;

    /// <summary>How many samples this renderer has been handed since it was created.</summary>
    public long SubmittedSampleCount => Interlocked.Read(ref _submittedSampleCount);

    public PlaybackOperationResult Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PlaybackOperationResult.Success;
    }

    public IAudioSubmit Submit(RawAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Interlocked.Add(ref _submittedSampleCount, buffer.SampleCount);
        return AcceptedSubmit.Instance;
    }

    public PlaybackOperationResult Flush()
    {
        return PlaybackOperationResult.Success;
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// The hand-off a null renderer returns: the block is taken whole and the hand-off is over before it starts,
    /// because there is no device with room to wait for. <see cref="IAudioSubmit.Current"/> is therefore never
    /// meaningful, and says so rather than naming a condition that is not happening.
    /// </summary>
    private sealed class AcceptedSubmit : IAudioSubmit
    {
        internal static AcceptedSubmit Instance { get; } = new();

        public bool MoveNext()
        {
            return false;
        }

        public AudioSubmitYield Current => throw new InvalidOperationException("A null renderer's hand-off never suspends.");

        public PlaybackOperationResult Result => PlaybackOperationResult.Success;

        public void Dispose()
        {
        }
    }
}
