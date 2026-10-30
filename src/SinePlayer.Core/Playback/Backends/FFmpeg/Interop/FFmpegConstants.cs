// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

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

    /// <summary><c>AV_PKT_DATA_DISPLAYMATRIX</c>: nine 16.16 fixed point numbers giving how to turn the picture.</summary>
    internal const int SideDataTypeDisplayMatrix = 5;

    /// <summary>
    /// <c>enum AVSubtitleType</c>: a piece of a subtitle event is nothing, a bitmap, plain text, or an ASS line. The
    /// enum counts from zero, so the three kinds that carry something are one, two and three.
    /// </summary>
    internal const int SubtitleTypeNone = 0;
    internal const int SubtitleTypeBitmap = 1;
    internal const int SubtitleTypeText = 2;
    internal const int SubtitleTypeAss = 3;
}
