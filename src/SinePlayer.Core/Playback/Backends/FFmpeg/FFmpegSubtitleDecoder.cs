// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Decodes text subtitles with libavcodec. Every text format is decoded into an ASS event line, which is what libavcodec
/// normalises them to, and the line is handed on as it came: the override tags and the style name in it are the
/// renderer's business, not the decoder's.
/// <para>
/// Subtitles take the legacy decode entry point. They are not frames, so there is no send/receive pair and nothing is
/// held between packets — a packet carries its own timing and produces its cues immediately. What a packet does not
/// carry is the length of the event: formats such as SubRip leave the display times at zero and put the duration in the
/// packet, which is the fallback used here.
/// </para>
/// <para>
/// Bitmap formats (PGS, VobSub) produce images instead of text and are turned away before a decoder is created.
/// </para>
/// </summary>
public sealed partial class FFmpegSubtitleDecoder : ISubtitleDecoder
{
    private readonly SubtitleStreamInfo _stream;
    private readonly string _decoderName;

    private nint _context;
    private bool _disposed;

    private FFmpegSubtitleDecoder(SubtitleStreamInfo stream, nint context, string decoderName)
    {
        _stream = stream;
        _context = context;
        _decoderName = decoderName;
    }

    public string Name => $"FFmpeg {_decoderName}";

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    public SubtitleCueFormat CueFormat => SubtitleCueFormat.Ass;

    /// <summary>
    /// Creates and opens a decoder for a subtitle stream of an FFmpeg source.
    /// </summary>
    public static bool TryCreate(IMediaSource? source, SubtitleStreamInfo stream, out FFmpegSubtitleDecoder? decoder, out string error)
    {
        decoder = null;

        if (source is null)
        {
            error = "A subtitle file of its own is not supported yet.";
            return false;
        }

        if (source is not FFmpegSource ffmpegSource)
        {
            error = "The subtitle stream does not come from an FFmpeg source.";
            return false;
        }

        try
        {
            FFmpegRuntime.EnsureInitialized();
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        if (!ffmpegSource.TryGetCodecParameters(stream.Index, out nint parameters))
        {
            error = $"Stream {stream.Index} exposes no codec parameters.";
            return false;
        }

        // Subtitles are looked up by codec id rather than by name: the container names a codec the way FFmpeg does
        // ("subrip"), while the decoder that reads it may be called something else ("srt").
        if (!TryFindDecoder(stream.CodecId, out nint codec, out string decoderName, out error))
        {
            return false;
        }

        nint context = FFmpegNative.avcodec_alloc_context3(codec);
        if (context == 0)
        {
            error = "Unable to allocate an FFmpeg codec context.";
            return false;
        }

        int result = FFmpegNative.avcodec_parameters_to_context(context, parameters);
        if (result < 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
            error = $"Unable to apply the stream's codec parameters: {FFmpegInterop.GetErrorMessage(result)}";
            return false;
        }

        result = FFmpegNative.avcodec_open2(context, codec, 0);
        if (result < 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
            error = $"Unable to open the subtitle decoder: {FFmpegInterop.GetErrorMessage(result)}";
            return false;
        }

        decoder = new FFmpegSubtitleDecoder(stream, context, decoderName);
        error = string.Empty;
        return true;
    }

    public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<SubtitleCue> cues)
    {
        List<SubtitleCue> decoded = [];
        cues = decoded;

        if (_disposed)
        {
            return OperationResult.Failure("The decoder has already been disposed.");
        }

        if (packet.Buffer is not FFmpegPacketBuffer packetBuffer)
        {
            return OperationResult.Failure("The packet was not produced by the FFmpeg backend.");
        }

        return DecodeSubtitle(packetBuffer.Packet, packet, decoded);
    }

    /// <summary>
    /// Nothing is ever held back: a subtitle packet is decoded whole and its cues are emitted at once, so there is
    /// never anything left to drain.
    /// </summary>
    public OperationResult Drain(out IReadOnlyList<SubtitleCue> cues)
    {
        cues = [];

        return _disposed
            ? OperationResult.Failure("The decoder has already been disposed.")
            : OperationResult.Success();
    }

    public OperationResult Flush()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The decoder has already been disposed.");
        }

        FFmpegNative.avcodec_flush_buffers(_context);
        return OperationResult.Success();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        nint context = _context;
        _context = 0;
        if (context != 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
        }
    }

    private unsafe OperationResult DecodeSubtitle(nint packet, MediaPacket mediaPacket, List<SubtitleCue> cues)
    {
        AVSubtitle subtitle = default;
        try
        {
            int gotSubtitle = 0;
            int result = FFmpegNative.avcodec_decode_subtitle2(_context, (nint)(&subtitle), (nint)(&gotSubtitle), packet);
            if (result < 0)
            {
                return OperationResult.Failure($"Unable to decode the subtitle: {FFmpegInterop.GetErrorMessage(result)}");
            }

            if (gotSubtitle == 0 || subtitle.RectCount == 0)
            {
                return OperationResult.Success();
            }

            string? text = CollectText(subtitle);
            if (string.IsNullOrEmpty(text))
            {
                // A bitmap event of a stream that was not recognised as bitmap: there is no text to carry.
                return OperationResult.Success();
            }

            TimeSpan start = mediaPacket.Timestamp + TimeSpan.FromMilliseconds(subtitle.StartDisplayTime);
            TimeSpan end = mediaPacket.Timestamp + TimeSpan.FromMilliseconds(subtitle.EndDisplayTime);
            if (end <= start)
            {
                // Some containers state no display time and leave the length to the packet's own duration.
                end = start + mediaPacket.Duration;
            }

            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                Text = text,
            });
            return OperationResult.Success();
        }
        finally
        {
            FFmpegNative.avsubtitle_free((nint)(&subtitle));
        }
    }

    private static unsafe string? CollectText(AVSubtitle subtitle)
    {
        var rects = (AVSubtitleRect**)subtitle.Rects;
        List<string>? lines = null;
        for (uint index = 0; index < subtitle.RectCount; index++)
        {
            AVSubtitleRect* rect = rects[index];
            if (rect == null)
            {
                continue;
            }

            string? line = rect->Type == FFmpegConstants.SubtitleTypeAss
                ? Marshal.PtrToStringUTF8(rect->Ass) ?? Marshal.PtrToStringUTF8(rect->Text)
                : Marshal.PtrToStringUTF8(rect->Text) ?? Marshal.PtrToStringUTF8(rect->Ass);
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            lines ??= [];
            lines.Add(line);
        }

        return lines is null ? null : string.Join("\n", lines);
    }

    private static unsafe bool TryFindDecoder(string codecId, out nint codec, out string decoderName, out string error)
    {
        codec = 0;
        decoderName = "subtitle";

        nint descriptorPointer = FFmpegNative.avcodec_descriptor_get_by_name(codecId);
        if (descriptorPointer == 0)
        {
            error = $"FFmpeg knows no codec named '{codecId}'.";
            return false;
        }

        var descriptor = (AVCodecDescriptor*)descriptorPointer;
        if (descriptor->Type != FFmpegConstants.MediaTypeSubtitle)
        {
            error = $"Codec '{codecId}' is not a subtitle codec.";
            return false;
        }

        codec = FFmpegNative.avcodec_find_decoder(descriptor->Id);
        if (codec == 0)
        {
            error = $"FFmpeg has no subtitle decoder for '{codecId}'.";
            return false;
        }

        nint name = FFmpegNative.avcodec_get_name(descriptor->Id);
        decoderName = name == 0 ? "subtitle" : Marshal.PtrToStringUTF8(name) ?? "subtitle";
        error = string.Empty;
        return true;
    }
}
