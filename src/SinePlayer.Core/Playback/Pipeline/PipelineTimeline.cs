// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed class PipelineTimeline
{
    private readonly SeqLock<EpochGate> _epochGate = new(new());

    private long _endingEpoch = -1;

    public EpochGate Gate => _epochGate.Read();

    public long Epoch => _epochGate.Read().Epoch;

    public long EndingEpoch
    {
        get => Interlocked.Read(ref _endingEpoch);
        set => Interlocked.Exchange(ref _endingEpoch, value);
    }

    public void AdvanceEpoch(TimeSpan position, SeekMode mode)
    {
        _epochGate.Write(x => new EpochGate()
        {
            Epoch = x.Epoch + 1,
            SeekPosition = position,
            SeekMode = mode,
        });
    }
}
