// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

public interface IVideoDecoder : IMediaDecoder
{
    /// <summary>Format of the frames this decoder produces. Becomes exact once frames start flowing.</summary>
    PixelFormat OutputFormat { get; }

    /// <summary>
    /// Decodes one packet. A packet may yield no frame at all, one frame, or several frames when the
    /// codec has reordering or field pairs.
    /// </summary>
    PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames);

    /// <summary>Emits the frames the decoder still holds after the stream ends.</summary>
    PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames);
}
