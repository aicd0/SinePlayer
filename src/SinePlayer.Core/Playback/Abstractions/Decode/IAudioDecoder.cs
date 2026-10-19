// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public interface IAudioDecoder : IMediaDecoder
{
    /// <summary>Format of the buffers this decoder produces. Becomes exact once buffers start flowing.</summary>
    AudioSampleFormat OutputFormat { get; }

    /// <summary>
    /// Decodes one packet. A packet normally yields one buffer but may yield several when the
    /// container packs multiple audio frames together.
    /// </summary>
    PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers);

    /// <summary>Emits the buffers the decoder still holds after the stream ends.</summary>
    PlaybackOperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers);
}
