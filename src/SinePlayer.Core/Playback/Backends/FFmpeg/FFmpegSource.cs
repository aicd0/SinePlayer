// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

internal sealed partial class FFmpegSource : IMediaSource
{
    private const string TAG = nameof(FFmpegSource);

    private const int DispositionDefault = 0x0001;
    private const int DispositionForced = 0x0040;
    private const int DispositionAttachedPicture = 0x0400;

    private const int MaximumAttachmentBytes = 16 * 1024 * 1024;
    private const long MaximumAttachmentTotalBytes = 64L * 1024 * 1024;

    /// <summary>How far a probe reads to decide where a seek would land: a landing block is within a packet or two of it.</summary>
    private const int ProbePacketLimit = 512;

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

    internal AVRational GetTimeBase(int streamIndex)
    {
        if (streamIndex < 0 || streamIndex >= _timeBases.Length)
        {
            return default;
        }

        return _timeBases[streamIndex];
    }

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

        int opened = FFmpegNative.avformat_open_input(out nint context, locator.Value, 0, 0);
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

    public bool TryChooseSeekStream(IReadOnlyList<int> streamIndexes, TimeSpan position, SeekMode mode, out int streamIndex)
    {
        streamIndex = -1;
        if (_disposed || streamIndexes.Count == 0)
        {
            return false;
        }

        List<int> candidates = [];
        List<TimeSpan> landings = [];
        TimeSpan earliest = TimeSpan.MaxValue;
        foreach (int candidate in streamIndexes)
        {
            if (!TryGetLanding(candidate, position, mode, out TimeSpan landing))
            {
                continue;
            }

            candidates.Add(candidate);
            landings.Add(landing);
            earliest = landing < earliest ? landing : earliest;
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // Equal landings are the ordinary case — streams encoded together keyframe together — and then the order the
        // container wrote them in decides, which only a seek can find out.
        for (int index = 0; index < candidates.Count; index++)
        {
            if (landings[index] == earliest && LeavesAKeyframeForEveryStream(candidates[index], streamIndexes, position, mode))
            {
                streamIndex = candidates[index];
                return true;
            }
        }

        // Nothing suits every stream: the earliest landing still leaves each chain's next keyframe closest to the target,
        // and it is what a source that cannot answer for the others would be asked for anyway.
        streamIndex = candidates[landings.IndexOf(earliest)];
        return true;
    }

    public OperationResult Seek(TimeSpan position, int streamIndex, SeekMode mode)
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

        int flags = mode == SeekMode.NextKeyframe ? 0 : FFmpegConstants.SeekFlagBackward;
        int result = FFmpegNative.av_seek_frame(_context, byStream ? streamIndex : -1, timestamp, flags);
        if (result == FFmpegConstants.ErrorRange && flags != 0)
        {
            // A backward seek promises an event at or before the target, and a subtitle queue refuses to break that
            // promise: it reads the whole file up front, so when nothing is at or before the target — a seek to the
            // start of a file whose first event is a second in, for one — the nearest event is after it and the queue
            // answers ERANGE (libavformat/subtitles.c, ff_subtitles_queue_seek). The honest landing there is the first
            // event, which is what a forward seek finds.
            result = FFmpegNative.av_seek_frame(_context, byStream ? streamIndex : -1, timestamp, 0);
        }

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

    private bool TryGetLanding(int streamIndex, TimeSpan position, SeekMode mode, out TimeSpan landing)
    {
        landing = position;
        if (streamIndex < 0 || streamIndex >= _timeBases.Length)
        {
            return false;
        }

        if (!Seek(position, streamIndex, mode).IsSuccessful)
        {
            return false;
        }

        while (true)
        {
            OperationResult read = TryReadPacket(out MediaPacket? packet);
            if (!read.IsSuccessful || packet is null)
            {
                return false;
            }

            using (packet)
            {
                if (packet.StreamIndex == streamIndex)
                {
                    landing = packet.Timestamp;
                    return true;
                }
            }
        }
    }

    private bool LeavesAKeyframeForEveryStream(int landingStream, IReadOnlyList<int> streamIndexes, TimeSpan position, SeekMode mode)
    {
        if (!Seek(position, landingStream, mode).IsSuccessful)
        {
            return false;
        }

        HashSet<int> pending = [.. streamIndexes];
        for (int read = 0; read < ProbePacketLimit && pending.Count > 0; read++)
        {
            OperationResult result = TryReadPacket(out MediaPacket? packet);
            if (!result.IsSuccessful || packet is null)
            {
                return false;
            }

            using (packet)
            {
                if (!pending.Remove(packet.StreamIndex))
                {
                    continue;
                }

                if (!packet.IsKeyFrame)
                {
                    return false;
                }
            }
        }

        return pending.Count == 0;
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
        List<MediaAttachment> attachments = [];
        long attachmentBytes = 0;
        timeBases = new AVRational[context->StreamCount];
        for (uint i = 0; i < context->StreamCount; i++)
        {
            AVStream* stream = streamPointers[i];
            timeBases[i] = stream->TimeBase;

            var parameters = (AVCodecParameters*)stream->CodecParameters;
            if (parameters is not null && parameters->CodecType == FFmpegConstants.MediaTypeAttachment)
            {
                MediaAttachment? attachment = ReadAttachment(stream, parameters, attachmentBytes);
                if (attachment is not null)
                {
                    attachments.Add(attachment);
                    attachmentBytes += attachment.Data.Length;
                }

                continue;
            }

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
            Attachments = [.. attachments],
        };
    }

    private static unsafe MediaAttachment? ReadAttachment(AVStream* stream, AVCodecParameters* parameters, long readSoFar)
    {
        if (parameters->Extradata == 0 || parameters->ExtradataSize <= 0)
        {
            return null;
        }

        if (parameters->ExtradataSize > MaximumAttachmentBytes)
        {
            PlaybackLogger.W(TAG, $"An attachment of {parameters->ExtradataSize} bytes is too large to be read");
            return null;
        }

        if (readSoFar + parameters->ExtradataSize > MaximumAttachmentTotalBytes)
        {
            PlaybackLogger.W(TAG, "The attachments of this file add up to more than can be held, so some were left behind");
            return null;
        }

        byte[] data = new byte[parameters->ExtradataSize];
        Marshal.Copy(parameters->Extradata, data, 0, data.Length);

        string? name = FFmpegInterop.GetMetadata(stream->Metadata, "filename");
        return new MediaAttachment
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"attachment-{stream->Index}" : name,
            MimeType = ResolveAttachmentMimeType(FFmpegInterop.GetMetadata(stream->Metadata, "mimetype"), name, data),
            Data = data,
        };
    }

    private static string? ResolveAttachmentMimeType(string? stated, string? name, byte[] data)
    {
        if (!string.IsNullOrWhiteSpace(stated))
        {
            return stated;
        }

        string? extension = Path.GetExtension(name)?.ToLowerInvariant();
        switch (extension)
        {
            case ".ttf":
                return "application/x-truetype-font";
            case ".otf":
                return "application/vnd.ms-opentype";
            case ".ttc":
            case ".otc":
                return "application/x-truetype-font-collection";
        }

        if (data.Length >= 4)
        {
            uint tag = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            switch (tag)
            {
                case 0x00010000:
                case 0x74727565: // "true"
                case 0x4F54544F: // "OTTO"
                    return "application/x-truetype-font";
                case 0x74746366: // "ttcf"
                    return "application/x-truetype-font-collection";
            }
        }

        return null;
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
                ScriptHeader = IsBitmapSubtitle(codecId) ? null : ReadScriptHeader(parameters),
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

    private static unsafe string? ReadScriptHeader(AVCodecParameters* parameters)
    {
        if (parameters->Extradata == 0 || parameters->ExtradataSize <= 0)
        {
            return null;
        }

        string? header = Marshal.PtrToStringUTF8(parameters->Extradata, parameters->ExtradataSize);
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }
}
