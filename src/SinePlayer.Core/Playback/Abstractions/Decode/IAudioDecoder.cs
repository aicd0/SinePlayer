// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public interface IAudioDecoder : IMediaDecoder
{
    AudioSampleFormat OutputFormat { get; }

    OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers);

    OperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers);
}
