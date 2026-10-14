// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Owns one <c>AVPacket</c>. Handing out a reference to the read packet rather than the read packet
/// itself means the demuxer can immediately reuse its own buffer, while the consumer still sees a
/// stable payload.
/// </summary>
internal sealed class FFmpegPacketBuffer : IMediaBuffer
{
    private nint _packet;

    internal FFmpegPacketBuffer(nint packet)
    {
        _packet = packet;
    }

    internal nint Packet => _packet;

    public long Length
    {
        get
        {
            nint packet = _packet;
            if (packet == 0)
            {
                return 0;
            }

            unsafe
            {
                return ((AVPacket*)packet)->Size;
            }
        }
    }

    public void Dispose()
    {
        nint packet = _packet;
        if (packet == 0)
        {
            return;
        }

        // av_packet_free releases the buffer and the packet itself.
        _packet = 0;
        FFmpegNative.av_packet_free(ref packet);
    }
}
