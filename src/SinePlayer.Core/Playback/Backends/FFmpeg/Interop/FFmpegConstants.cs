// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

internal static class FFmpegConstants
{
    /// <summary>FFmpeg time base used by <c>AVFormatContext.duration</c>: microseconds.</summary>
    internal const int TimeBase = 1000000;

    /// <summary>Value stored in a timestamp field that carries no information.</summary>
    internal const long NoPtsValue = long.MinValue;

    internal const int MediaTypeVideo = 0;
    internal const int MediaTypeAudio = 1;
    internal const int MediaTypeData = 2;
    internal const int MediaTypeSubtitle = 3;
    internal const int MediaTypeAttachment = 4;

    internal const int SeekFlagBackward = 1;

    /// <summary>Packet carries a key frame.</summary>
    internal const int PacketFlagKey = 1;

    /// <summary><c>AVERROR_EOF</c>, the four character tag "EOF ".</summary>
    internal const int ErrorEndOfStream = -0x20464F45;

    /// <summary><c>AVERROR(EAGAIN)</c>: the call would block, try again after the other direction made progress.</summary>
    internal const int ErrorAgain = -11;
}
