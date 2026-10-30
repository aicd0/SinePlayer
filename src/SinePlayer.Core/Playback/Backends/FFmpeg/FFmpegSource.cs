// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

public sealed partial class FFmpegSource : IMediaSource
{
    private const int DispositionDefault = 0x0001;
    private const int DispositionForced = 0x0040;
    private const int DispositionAttachedPicture = 0x0400;

    private static readonly string[] sBitmapSubtitleCodecs =
    [
        "dvd_subtitle",
        "hdmv_pgs_subtitle",
        "dvb_subtitle",
        "xsub",
        "dvb_teletext",
    ];

    private readonly MediaInfo _info;

    /// <summary>Time base per stream, indexed by stream index, needed to convert packet timestamps.</summary>
    private readonly AVRational[] _timeBases;

    private nint _context;
    private nint _readPacket;
    private bool _disposed;

    private FFmpegSource(nint context, nint readPacket, MediaInfo info, AVRational[] timeBases)
    {
        _context = context;
        _readPacket = readPacket;
        _info = info;
        _timeBases = timeBases;
    }

    public MediaInfo Info => _info;

    public bool CanSeek => true;

    /// <summary>Time base of a stream, needed to interpret packet and frame timestamps.</summary>
    internal AVRational GetTimeBase(int streamIndex)
    {
        if (streamIndex < 0 || streamIndex >= _timeBases.Length)
        {
            return default;
        }

        return _timeBases[streamIndex];
    }

    /// <summary>
    /// Borrows the codec parameters of a stream. The pointer is owned by the open context and stays
    /// valid until this source is disposed; callers must copy what they need (which is what
    /// <c>avcodec_parameters_to_context</c> does).
    /// </summary>
    internal unsafe bool TryGetCodecParameters(int streamIndex, out nint parameters)
    {
        parameters = 0;
        if (_disposed || _context == 0 || streamIndex < 0 || streamIndex >= _timeBases.Length)
        {
            return false;
        }

        var context = (AVFormatContext*)_context;
        var streams = (AVStream**)context->Streams;
        AVStream* stream = streams[streamIndex];
        if (stream == null || stream->CodecParameters == 0)
        {
            return false;
        }

        parameters = stream->CodecParameters;
        return true;
    }

    /// <summary>
    /// Opens a locator with libavformat and reads its stream table.
    /// </summary>
    public static OperationResult<FFmpegSource> TryOpen(MediaLocator locator)
    {
        OperationResultCreator<FFmpegSource> result = new();

        try
        {
            FFmpegRuntime.EnsureInitialized();
        }
        catch (Exception exception)
        {
            return result.Failure(exception);
        }

        nint context = 0;
        int opened = FFmpegNative.avformat_open_input(out context, locator.Value, 0, 0);
        if (opened < 0)
        {
            return result.Failure($"Unable to open '{locator.Value}': {FFmpegInterop.GetErrorMessage(opened)}");
        }

        int stacked = FFmpegNative.avformat_find_stream_info(context, 0);
        if (stacked < 0)
        {
            FFmpegNative.avformat_close_input(ref context);
            return result.Failure($"Unable to read stream information from '{locator.Value}': {FFmpegInterop.GetErrorMessage(stacked)}");
        }

        nint readPacket = FFmpegNative.av_packet_alloc();
        if (readPacket == 0)
        {
            FFmpegNative.avformat_close_input(ref context);
            return result.Failure("Unable to allocate an FFmpeg packet.");
        }

        MediaInfo info = CreateInfo(context, out AVRational[] timeBases);
        return result.Success(new FFmpegSource(context, readPacket, info, timeBases));
    }

    public OperationResult Seek(TimeSpan position, int streamIndex)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The media source has already been disposed.");
        }

        // A landing by a chosen stream is expressed in that stream's own time base; only this source's own choice is
        // in the flat one. The choice matters because a keyframe belongs to a stream: by the picture, a position past
        // its end lands on its last keyframe, while by the sound — which has no keyframes — it lands within a packet.
        bool byStream = streamIndex >= 0 && streamIndex < _timeBases.Length;
        long timestamp;
        if (byStream)
        {
            AVRational timeBase = _timeBases[streamIndex];
            timestamp = timeBase.Numerator > 0
                ? (long)(position.TotalSeconds * timeBase.Denominator / timeBase.Numerator)
                : 0;
        }
        else
        {
            timestamp = (long)(position.TotalSeconds * FFmpegConstants.TimeBase);
        }

        int result = FFmpegNative.av_seek_frame(_context, byStream ? streamIndex : -1, timestamp, FFmpegConstants.SeekFlagBackward);
        if (result < 0)
        {
            return OperationResult.Failure($"Unable to seek to {position}: {FFmpegInterop.GetErrorMessage(result)}");
        }

        return OperationResult.Success();
    }

    public OperationResult TryReadPacket(out MediaPacket? packet)
    {
        packet = null;
        if (_disposed)
        {
            return OperationResult.Failure("The media source has already been disposed.");
        }

        int result = FFmpegNative.av_read_frame(_context, _readPacket);
        if (result == FFmpegConstants.ErrorEndOfStream)
        {
            return OperationResult.Success();
        }

        if (result < 0)
        {
            return OperationResult.Failure($"Unable to read the next packet: {FFmpegInterop.GetErrorMessage(result)}");
        }

        nint owned = FFmpegNative.av_packet_alloc();
        if (owned == 0)
        {
            FFmpegNative.av_packet_unref(_readPacket);
            return OperationResult.Failure("Unable to allocate an FFmpeg packet.");
        }

        int referenceResult = FFmpegNative.av_packet_ref(owned, _readPacket);
        FFmpegNative.av_packet_unref(_readPacket);
        if (referenceResult < 0)
        {
            FFmpegNative.av_packet_free(ref owned);
            return OperationResult.Failure($"Unable to retain packet data: {FFmpegInterop.GetErrorMessage(referenceResult)}");
        }

        packet = CreatePacket(owned);
        return OperationResult.Success();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        nint readPacket = _readPacket;
        _readPacket = 0;
        if (readPacket != 0)
        {
            FFmpegNative.av_packet_free(ref readPacket);
        }

        nint context = _context;
        _context = 0;
        if (context != 0)
        {
            FFmpegNative.avformat_close_input(ref context);
        }
    }

    private unsafe MediaPacket CreatePacket(nint packetPointer)
    {
        var packet = (AVPacket*)packetPointer;
        int streamIndex = packet->StreamIndex;
        AVRational timeBase = streamIndex >= 0 && streamIndex < _timeBases.Length ? _timeBases[streamIndex] : default;
        long timestamp = packet->Pts == FFmpegConstants.NoPtsValue ? packet->Dts : packet->Pts;

        return new MediaPacket
        {
            StreamIndex = streamIndex,
            Timestamp = FFmpegInterop.ToTimeSpan(timestamp, timeBase),
            Duration = FFmpegInterop.ToTimeSpan(packet->Duration, timeBase),
            IsKeyFrame = (packet->Flags & FFmpegConstants.PacketFlagKey) != 0,
            Buffer = new FFmpegPacketBuffer(packetPointer),
        };
    }

    private static unsafe MediaInfo CreateInfo(nint contextPointer, out AVRational[] timeBases)
    {
        var context = (AVFormatContext*)contextPointer;
        var streamPointers = (AVStream**)context->Streams;

        List<MediaStreamInfo> streams = [];
        timeBases = new AVRational[context->StreamCount];
        for (uint i = 0; i < context->StreamCount; i++)
        {
            AVStream* stream = streamPointers[i];
            timeBases[i] = stream->TimeBase;

            MediaStreamInfo? streamInfo = CreateStreamInfo(stream);
            if (streamInfo is not null)
            {
                streams.Add(streamInfo);
            }
        }

        string containerId = FFmpegInterop.ToUtf8String(((AVInputFormat*)context->InputFormat)->Name) ?? "unknown";
        TimeSpan duration = context->Duration > 0 ? TimeSpan.FromTicks(context->Duration * 10) : TimeSpan.Zero;

        return new MediaInfo
        {
            ContainerId = containerId,
            Duration = duration,
            Bitrate = context->BitRate,
            Streams = streams,
        };
    }

    private static unsafe MediaStreamInfo? CreateStreamInfo(AVStream* stream)
    {
        var parameters = (AVCodecParameters*)stream->CodecParameters;
        if (parameters == null)
        {
            return null;
        }

        if ((stream->Disposition & DispositionAttachedPicture) != 0)
        {
            return null;
        }

        MediaStreamKind kind = FFmpegInterop.ToStreamKind(parameters->CodecType);
        string codecId = FFmpegInterop.GetCodecId(parameters->CodecId);

        TimeSpan duration = stream->Duration > 0
            ? FFmpegInterop.ToTimeSpan(stream->Duration, stream->TimeBase)
            : FFmpegInterop.ParseDurationTag(FFmpegInterop.GetMetadata(stream->Metadata, "DURATION")) ?? TimeSpan.Zero;

        return kind switch
        {
            MediaStreamKind.Video => new VideoStreamInfo
            {
                Index = stream->Index,
                CodecId = codecId,
                Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                Bitrate = parameters->BitRate,
                IsDefault = (stream->Disposition & DispositionDefault) != 0,
                Duration = duration,
                Width = parameters->Width,
                Height = parameters->Height,
                FrameRate = ResolveFrameRate(stream, parameters),
                PixelFormat = FFmpegInterop.ToPixelFormat(parameters->Format),
                Orientation = ResolveOrientation(parameters),
                BitDepth = parameters->BitsPerRawSample,
                Colour = FFmpegInterop.ToColourInfo(parameters->ColorPrimaries, parameters->ColorTransfer, parameters->ColorRange),
            },
            MediaStreamKind.Audio => new AudioStreamInfo
            {
                Index = stream->Index,
                CodecId = codecId,
                Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                Bitrate = parameters->BitRate,
                IsDefault = (stream->Disposition & DispositionDefault) != 0,
                Duration = duration,
                SampleRate = parameters->SampleRate,
                ChannelCount = parameters->ChannelLayout.ChannelCount,
                ChannelLayout = FFmpegInterop.ToChannelLayout(parameters->ChannelLayout),
                SampleFormat = FFmpegInterop.ToSampleFormat(parameters->Format),
            },
            MediaStreamKind.Subtitle => new SubtitleStreamInfo
            {
                Index = stream->Index,
                CodecId = codecId,
                Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                Bitrate = parameters->BitRate,
                IsDefault = (stream->Disposition & DispositionDefault) != 0,
                Duration = duration,
                IsForced = (stream->Disposition & DispositionForced) != 0,
                IsBitmap = IsBitmapSubtitle(codecId),
            },
            _ => null,
        };
    }

    private static unsafe double ResolveFrameRate(AVStream* stream, AVCodecParameters* parameters)
    {
        double average = FFmpegInterop.ToRate(stream->AverageFrameRate);
        return average > 0 ? average : FFmpegInterop.ToRate(parameters->FrameRate);
    }

    private static unsafe VideoOrientation ResolveOrientation(AVCodecParameters* parameters)
    {
        if (parameters->CodedSideDataCount <= 0 || parameters->CodedSideData == 0)
        {
            return VideoOrientation.Identity;
        }

        var entries = (AVPacketSideData*)parameters->CodedSideData;
        for (int i = 0; i < parameters->CodedSideDataCount; i++)
        {
            if (entries[i].Type != FFmpegConstants.SideDataTypeDisplayMatrix || entries[i].Data == 0)
            {
                continue;
            }

            if (entries[i].Size < 9 * sizeof(int))
            {
                return VideoOrientation.Identity;
            }

            int* matrix = (int*)entries[i].Data;
            return FFmpegInterop.ToOrientation(matrix);
        }

        return VideoOrientation.Identity;
    }

    private static bool IsBitmapSubtitle(string codecId)
    {
        foreach (string candidate in sBitmapSubtitleCodecs)
        {
            if (string.Equals(candidate, codecId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
