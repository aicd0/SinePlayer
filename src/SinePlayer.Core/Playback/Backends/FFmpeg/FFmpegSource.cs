// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// A media source backed by an FFmpeg <c>AVFormatContext</c>. libavformat performs the byte access and
/// the container parsing in one step, which is why the source and demuxer stages of the design are a
/// single component here.
/// </summary>
public sealed class FFmpegSource : IMediaSource
{
    private const int DispositionDefault = 0x0001;
    private const int DispositionForced = 0x0040;

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
    public static bool TryOpen(MediaLocator locator, out FFmpegSource? source, out string error)
    {
        source = null;

        try
        {
            FFmpegRuntime.EnsureInitialized();
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        nint context = 0;
        int result = FFmpegNative.avformat_open_input(out context, locator.Value, 0, 0);
        if (result < 0)
        {
            error = $"Unable to open '{locator.Value}': {FFmpegInterop.GetErrorMessage(result)}";
            return false;
        }

        result = FFmpegNative.avformat_find_stream_info(context, 0);
        if (result < 0)
        {
            FFmpegNative.avformat_close_input(ref context);
            error = $"Unable to read stream information from '{locator.Value}': {FFmpegInterop.GetErrorMessage(result)}";
            return false;
        }

        nint readPacket = FFmpegNative.av_packet_alloc();
        if (readPacket == 0)
        {
            FFmpegNative.avformat_close_input(ref context);
            error = "Unable to allocate an FFmpeg packet.";
            return false;
        }

        MediaInfo info = CreateInfo(context, out AVRational[] timeBases);
        source = new FFmpegSource(context, readPacket, info, timeBases);
        error = string.Empty;
        return true;
    }

    public PlaybackOperationResult Seek(TimeSpan position)
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The media source has already been disposed.");
        }

        long timestamp = (long)(position.TotalSeconds * FFmpegConstants.TimeBase);
        int result = FFmpegNative.av_seek_frame(_context, -1, timestamp, FFmpegConstants.SeekFlagBackward);
        if (result < 0)
        {
            return PlaybackOperationResult.Failure($"Unable to seek to {position}: {FFmpegInterop.GetErrorMessage(result)}");
        }

        return PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult TryReadPacket(out MediaPacket? packet)
    {
        packet = null;
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The media source has already been disposed.");
        }

        int result = FFmpegNative.av_read_frame(_context, _readPacket);
        if (result == FFmpegConstants.ErrorEndOfStream)
        {
            return PlaybackOperationResult.Success;
        }

        if (result < 0)
        {
            return PlaybackOperationResult.Failure($"Unable to read the next packet: {FFmpegInterop.GetErrorMessage(result)}");
        }

        nint owned = FFmpegNative.av_packet_alloc();
        if (owned == 0)
        {
            FFmpegNative.av_packet_unref(_readPacket);
            return PlaybackOperationResult.Failure("Unable to allocate an FFmpeg packet.");
        }

        int referenceResult = FFmpegNative.av_packet_ref(owned, _readPacket);
        FFmpegNative.av_packet_unref(_readPacket);
        if (referenceResult < 0)
        {
            FFmpegNative.av_packet_free(ref owned);
            return PlaybackOperationResult.Failure($"Unable to retain packet data: {FFmpegInterop.GetErrorMessage(referenceResult)}");
        }

        packet = CreatePacket(owned);
        return PlaybackOperationResult.Success;
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

        MediaStreamKind kind = FFmpegInterop.ToStreamKind(parameters->CodecType);
        string codecId = FFmpegInterop.GetCodecId(parameters->CodecId);

        switch (kind)
        {
            case MediaStreamKind.Video:
                return new VideoStreamInfo
                {
                    Index = stream->Index,
                    CodecId = codecId,
                    Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                    Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                    Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                    Bitrate = parameters->BitRate,
                    IsDefault = (stream->Disposition & DispositionDefault) != 0,
                    Width = parameters->Width,
                    Height = parameters->Height,
                    FrameRate = ResolveFrameRate(stream, parameters),
                    PixelFormat = FFmpegInterop.ToPixelFormat(parameters->Format),
                    BitDepth = parameters->BitsPerRawSample,
                };

            case MediaStreamKind.Audio:
                return new AudioStreamInfo
                {
                    Index = stream->Index,
                    CodecId = codecId,
                    Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                    Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                    Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                    Bitrate = parameters->BitRate,
                    IsDefault = (stream->Disposition & DispositionDefault) != 0,
                    SampleRate = parameters->SampleRate,
                    ChannelCount = parameters->ChannelLayout.ChannelCount,
                    ChannelLayout = FFmpegInterop.ToChannelLayout(parameters->ChannelLayout.ChannelCount),
                    SampleFormat = FFmpegInterop.ToSampleFormat(parameters->Format),
                };

            case MediaStreamKind.Subtitle:
                return new SubtitleStreamInfo
                {
                    Index = stream->Index,
                    CodecId = codecId,
                    Profile = FFmpegInterop.GetProfileName(parameters->CodecId, parameters->Profile),
                    Language = FFmpegInterop.GetMetadata(stream->Metadata, "language"),
                    Title = FFmpegInterop.GetMetadata(stream->Metadata, "title"),
                    Bitrate = parameters->BitRate,
                    IsDefault = (stream->Disposition & DispositionDefault) != 0,
                    IsForced = (stream->Disposition & DispositionForced) != 0,
                    IsBitmap = IsBitmapSubtitle(codecId),
                };

            default:
                return null;
        }
    }

    private static unsafe double ResolveFrameRate(AVStream* stream, AVCodecParameters* parameters)
    {
        double average = FFmpegInterop.ToRate(stream->AverageFrameRate);
        return average > 0 ? average : FFmpegInterop.ToRate(parameters->FrameRate);
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
