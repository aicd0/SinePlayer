// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

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
