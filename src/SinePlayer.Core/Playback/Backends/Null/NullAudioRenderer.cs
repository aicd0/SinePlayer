// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed partial class NullAudioRenderer : IAudioRenderer
{
    private readonly IPlaybackClock _clock = new StopwatchClock();

    private long _submittedSampleCount;

    public string Name => "Null";

    public long UnderrunCount => 0;

    public IPlaybackClock Clock => _clock;

    public long QueuedSampleCount => 0;

    public long SubmittedSampleCount => Interlocked.Read(ref _submittedSampleCount);

    public OperationResult Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OperationResult.Success();
    }

    public IAudioSubmit Submit(RawAudioBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Interlocked.Add(ref _submittedSampleCount, buffer.SampleCount);
        return AcceptedSubmit.Instance;
    }

    public OperationResult Flush()
    {
        return OperationResult.Success();
    }

    public void Dispose()
    {
    }

    private sealed partial class AcceptedSubmit : IAudioSubmit
    {
        internal static AcceptedSubmit Instance { get; } = new();

        public bool MoveNext()
        {
            return false;
        }

        public AudioSubmitYield Current => throw new InvalidOperationException("A null renderer's hand-off never suspends.");

        public OperationResult Result => OperationResult.Success();

        public void Dispose()
        {
        }
    }
}
