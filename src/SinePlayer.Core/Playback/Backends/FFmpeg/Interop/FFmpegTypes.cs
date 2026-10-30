// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

//
// Field layouts mirror the headers of the pinned FFmpeg build. Only the fields the engine reads are
// declared, but every preceding field is present because the offsets are what make the declared
// fields addressable. Verify against the headers before changing anything here.
//

[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int Numerator;
    public int Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVChannelLayout
{
    public int Order;
    public int ChannelCount;

    /// <summary>Union of a channel mask and a custom channel map; both are 8 bytes wide.</summary>
    public nint Layout;
    public nint Opaque;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVCodecParameters
{
    public int CodecType;
    public int CodecId;
    public uint CodecTag;
    public nint Extradata;
    public int ExtradataSize;
    public nint CodedSideData;
    public int CodedSideDataCount;
    public int Format;
    public long BitRate;
    public int BitsPerCodedSample;
    public int BitsPerRawSample;
    public int Profile;
    public int Level;
    public int Width;
    public int Height;
    public AVRational SampleAspectRatio;
    public AVRational FrameRate;
    public int FieldOrder;
    public int ColorRange;
    public int ColorPrimaries;
    public int ColorTransfer;
    public int ColorSpace;
    public int ChromaLocation;
    public int VideoDelay;
    public AVChannelLayout ChannelLayout;
    public int SampleRate;
    public int BlockAlign;
    public int FrameSize;
    public int InitialPadding;
    public int TrailingPadding;
    public int SeekPreroll;
    public int AlphaMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVPacket
{
    public nint Buffer;
    public long Pts;
    public long Dts;
    public nint Data;
    public int Size;
    public int StreamIndex;
    public int Flags;
    public nint SideData;
    public int SideDataCount;
    public long Duration;
    public long Position;
    public nint Opaque;
    public nint OpaqueRef;
    public AVRational TimeBase;
}

/// <summary>
/// The leading part of <c>AVCodecContext</c>, down to the <c>get_format</c> callback. It is declared
/// field by field up to there because that callback is written into the context before the decoder is
/// opened, and a field is only addressable when everything in front of it is present.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVCodecContext
{
    public nint AvClass;
    public int LogLevelOffset;
    public int CodecType;
    public nint Codec;
    public int CodecId;
    public uint CodecTag;
    public nint PrivateData;
    public nint Internal;
    public nint Opaque;
    public long BitRate;
    public int Flags;
    public int Flags2;
    public nint Extradata;
    public int ExtradataSize;
    public AVRational TimeBase;
    public AVRational PacketTimeBase;
    public AVRational FrameRate;
    public int Delay;
    public int Width;
    public int Height;
    public int CodedWidth;
    public int CodedHeight;
    public AVRational SampleAspectRatio;
    public int PixelFormat;
    public int SoftwarePixelFormat;
    public int ColorPrimaries;
    public int ColorTransfer;
    public int ColorSpace;
    public int ColorRange;
    public int ChromaLocation;
    public int FieldOrder;
    public int ReferenceCount;
    public int HasBFrames;
    public int SliceFlags;
    public nint DrawHorizontalBand;

    /// <summary><c>enum AVPixelFormat (*get_format)(AVCodecContext *, const enum AVPixelFormat *)</c>.</summary>
    public nint GetFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVStream
{
    public nint AvClass;
    public int Index;
    public int Id;
    public nint CodecParameters;
    public nint PrivateData;
    public AVRational TimeBase;
    public long StartTime;
    public long Duration;
    public long FrameCount;
    public int Disposition;
    public int Discard;
    public AVRational SampleAspectRatio;
    public nint Metadata;
    public AVRational AverageFrameRate;
}

/// <summary>
/// One piece of <c>AVPacketSideData</c>: an out of band payload such as the display matrix. The same array type
/// backs <c>AVCodecParameters::coded_side_data</c>, which is where a container puts the display matrix.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVPacketSideData
{
    public nint Data;
    public nuint Size;
    public int Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVBufferRef
{
    public nint Buffer;
    public nint Data;
    public int Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVHWDeviceContext
{
    public nint AvClass;
    public int Type;
    public nint HardwareContext;
    public nint Free;
    public nint UserOpaque;
}

/// <summary>
/// The frames context libavcodec decodes hardware frames into. Its <c>sw_format</c> is the layout of the
/// pixels inside the texture, which is what a renderer has to sample them as.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVHWFramesContext
{
    public nint AvClass;
    public nint DeviceRef;
    public nint DeviceContext;
    public nint HardwareContext;
    public nint Internal;
    public nint Opaque;
    public nint GetBuffer;
    public int InitialPoolSize;
    public int Format;
    public int SoftwareFormat;
    public int Width;
    public int Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVCodecDescriptor
{
    /// <summary><c>enum AVCodecID</c>.</summary>
    public int Id;

    /// <summary><c>enum AVMediaType</c>.</summary>
    public int Type;

    public nint Name;
}

/// <summary>
/// One decoded subtitle event. Its display times are milliseconds <em>relative to the packet that carried it</em>, not
/// absolute positions, which is what makes a subtitle packet readable on its own.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVSubtitle
{
    /// <summary>Zero for a graphics event; decoders set it to a non-zero value for text.</summary>
    public ushort Format;

    public uint StartDisplayTime;

    public uint EndDisplayTime;

    public uint RectCount;

    public nint Rects;

    public long Pts;
}

/// <summary>
/// One piece of a subtitle event: a text line, an ASS line, or a bitmap. A text line is either plain text or an ASS
/// dialogue line whose override tags are left for the renderer to interpret.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVSubtitleRect
{
    public int X;
    public int Y;
    public int W;
    public int H;
    public int ColorCount;

    /// <summary><c>uint8_t *data[4]</c>.</summary>
    public fixed long Data[4];

    /// <summary><c>int linesize[4]</c>.</summary>
    public fixed int LineSize[4];

    public int Flags;

    /// <summary>0 = nothing, 1 = bitmap, 2 = text, 3 = ASS.</summary>
    public int Type;

    public nint Text;

    public nint Ass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVInputFormat
{
    public nint Name;
    public nint LongName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVDictionaryEntry
{
    public nint Key;
    public nint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFrame
{
    /// <summary><c>uint8_t *data[8]</c>.</summary>
    public fixed long Data[8];

    /// <summary><c>int linesize[8]</c>.</summary>
    public fixed int LineSize[8];

    public nint ExtendedData;
    public int Width;
    public int Height;
    public int SampleCount;
    public int Format;
    public int PictureType;
    public AVRational SampleAspectRatio;
    public long Pts;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVFormatContext
{
    public nint AvClass;
    public nint InputFormat;
    public nint OutputFormat;
    public nint PrivateData;
    public nint IoContext;
    public int ContextFlags;
    public uint StreamCount;
    public nint Streams;
    public uint StreamGroupCount;
    public nint StreamGroups;
    public uint ChapterCount;
    public nint Chapters;
    public nint Url;
    public long StartTime;
    public long Duration;
    public long BitRate;
}
