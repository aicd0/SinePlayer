// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Source;

public interface IMediaSource : IDisposable
{
    MediaInfo Info { get; }

    bool CanSeek { get; }

    bool TryChooseSeekStream(IReadOnlyList<int> streamIndexes, TimeSpan position, SeekMode mode, out int streamIndex);

    OperationResult Seek(TimeSpan position, int streamIndex, SeekMode mode);

    OperationResult TryReadPacket(out MediaPacket? packet);
}
