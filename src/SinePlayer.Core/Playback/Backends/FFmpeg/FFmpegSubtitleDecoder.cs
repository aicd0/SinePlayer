// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

public sealed partial class FFmpegSubtitleDecoder : ISubtitleDecoder
{
    private readonly SubtitleStreamInfo _stream;
    private readonly string _decoderName;
    private readonly SubtitleCueFormat _cueFormat;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private nint _context;
    private bool _disposed;

    private FFmpegSubtitleDecoder(SubtitleStreamInfo stream, nint context, string decoderName, SubtitleCueFormat cueFormat, int canvasWidth, int canvasHeight)
    {
        _stream = stream;
        _context = context;
        _decoderName = decoderName;
        _cueFormat = cueFormat;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
    }

    public string Name => $"FFmpeg {_decoderName}";

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    public SubtitleCueFormat CueFormat => _cueFormat;

    public static OperationResult<FFmpegSubtitleDecoder> Create(IMediaSource? source, SubtitleStreamInfo stream)
    {
        OperationResultCreator<FFmpegSubtitleDecoder> result = new();

        if (source is null)
        {
            return result.Failure("A subtitle file of its own is not supported yet.");
        }

        if (source is not FFmpegSource ffmpegSource)
        {
            return result.Failure("The subtitle stream does not come from an FFmpeg source.");
        }

        try
        {
            FFmpegRuntime.EnsureInitialized();
        }
        catch (Exception exception)
        {
            return result.Failure(exception);
        }

        if (!ffmpegSource.TryGetCodecParameters(stream.Index, out nint parameters))
        {
            return result.Failure($"Stream {stream.Index} exposes no codec parameters.");
        }

        // Subtitles are looked up by codec id rather than by name: the container names a codec the way FFmpeg does
        // ("subrip"), while the decoder that reads it may be called something else ("srt").
        OperationResult<(nint Codec, string Name)> found = TryFindDecoder(stream.CodecId);
        if (!found.IsSuccessful)
        {
            return result.Failure(found);
        }

        (nint codec, string decoderName) = found.Result;

        nint context = FFmpegNative.avcodec_alloc_context3(codec);
        if (context == 0)
        {
            return result.Failure("Unable to allocate an FFmpeg codec context.");
        }

        int applied = FFmpegNative.avcodec_parameters_to_context(context, parameters);
        if (applied < 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
            return result.Failure($"Unable to apply the stream's codec parameters: {FFmpegInterop.GetErrorMessage(applied)}");
        }

        int opened = FFmpegNative.avcodec_open2(context, codec, 0);
        if (opened < 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
            return result.Failure($"Unable to open the subtitle decoder: {FFmpegInterop.GetErrorMessage(opened)}");
        }

        int canvasWidth = 0;
        int canvasHeight = 0;
        if (stream.IsBitmap)
        {
            unsafe
            {
                var codecParameters = (AVCodecParameters*)parameters;
                canvasWidth = codecParameters->Width;
                canvasHeight = codecParameters->Height;
            }
        }

        return result.Success(new FFmpegSubtitleDecoder(
            stream,
            context,
            decoderName,
            stream.IsBitmap ? SubtitleCueFormat.BitmapBgra : SubtitleCueFormat.Ass,
            canvasWidth,
            canvasHeight));
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

            if (gotSubtitle == 0)
            {
                return OperationResult.Success();
            }

            TimeSpan start = mediaPacket.Timestamp + TimeSpan.FromMilliseconds(subtitle.StartDisplayTime);
            TimeSpan end = ReadEnd(subtitle, mediaPacket, start);

            if (_cueFormat == SubtitleCueFormat.BitmapBgra)
            {
                if (subtitle.RectCount == 0)
                {
                    // A display set with nothing in it is how a bitmap track takes the picture away, and a decoder
                    // reports it as an event like any other. A cue with no length and nothing to draw says exactly
                    // that: it is never on screen, it only ends what was there before it.
                    cues.Add(new SubtitleCue
                    {
                        Start = start,
                        End = start,
                    });
                    return OperationResult.Success();
                }

                SubtitleImage? image = CollectImage(subtitle);
                if (image is null)
                {
                    return OperationResult.Success();
                }

                cues.Add(new SubtitleCue
                {
                    Start = start,
                    End = end,
                    Image = image,
                });
                return OperationResult.Success();
            }

            if (subtitle.RectCount == 0)
            {
                return OperationResult.Success();
            }

            string? text = CollectText(subtitle);
            if (string.IsNullOrEmpty(text))
            {
                // A bitmap event of a stream that was not recognised as bitmap: there is no text to carry.
                return OperationResult.Success();
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

    /// <summary>
    /// When an event stops being on screen. A track may state it, in which case that is the answer; a bitmap track
    /// usually does not, and it says so with <see cref="uint.MaxValue"/> — the end is then whatever event replaces it,
    /// which only the reader of the whole track knows, so the cue is left open.
    /// </summary>
    internal static TimeSpan ReadEnd(AVSubtitle subtitle, MediaPacket mediaPacket, TimeSpan start)
    {
        if (subtitle.EndDisplayTime == uint.MaxValue)
        {
            // What a bitmap track says instead of an end: the event after this one is what takes it away, and only the
            // reader of the whole track knows when that is.
            return SubtitleCue.OpenEnd;
        }

        TimeSpan stated = mediaPacket.Timestamp + TimeSpan.FromMilliseconds(subtitle.EndDisplayTime);
        if (stated > start)
        {
            return stated;
        }

        // Some containers state no display time and leave the length to the packet's own duration.
        return mediaPacket.Duration > TimeSpan.Zero
            ? start + mediaPacket.Duration
            : SubtitleCue.OpenEnd;
    }

    /// <summary>
    /// Turns one event's rectangles into a single picture: the smallest image that holds all of them, with the placement
    /// that puts it back where it belongs.
    /// <para>
    /// A bitmap event often arrives as several rectangles — a DVD subtitle is close to one line per rectangle — but a
    /// renderer draws a cue it is given, so they are composed into one image. Cropping to their bounding box rather than
    /// to the frame keeps a cue at the size of the words it holds: a 1920×1080 frame per cue would be eight megabytes
    /// for a line of text, kept for as long as the cue is.
    /// </para>
    /// </summary>
    private unsafe SubtitleImage? CollectImage(AVSubtitle subtitle)
    {
        var rects = (AVSubtitleRect**)subtitle.Rects;

        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;
        for (uint index = 0; index < subtitle.RectCount; index++)
        {
            AVSubtitleRect* rect = rects[index];
            if (rect == null || !IsDrawable(rect))
            {
                continue;
            }

            left = Math.Min(left, rect->X);
            top = Math.Min(top, rect->Y);
            right = Math.Max(right, rect->X + rect->W);
            bottom = Math.Max(bottom, rect->Y + rect->H);
        }

        if (left >= right || top >= bottom)
        {
            // An event with nothing drawable in it is a cue that clears the screen: the packet carries a timing and says
            // no more than that. Nothing is added, which leaves the cues already there to expire on their own.
            return null;
        }

        // The frame the rectangles are positioned inside, which is what turns pixels into fractions of the picture. The
        // decoder knows it — a bitmap codec states it in the event itself, and FFmpeg records it on the context — while
        // the stream's codec parameters often leave it at zero (a Matroska PGS track says nothing), so the parameters
        // are only the fallback and the rectangles the last resort.
        int canvasWidth = ReadCanvasWidth(right);
        int canvasHeight = ReadCanvasHeight(bottom);

        int width = right - left;
        int height = bottom - top;
        int stride = width * 4;
        nint pixels = Marshal.AllocHGlobal(stride * height);
        bool handedOver = false;
        try
        {
            // Starts transparent: a cue covers part of the picture, not all of it.
            Span<byte> image = new((void*)pixels, stride * height);
            image.Clear();

            for (uint index = 0; index < subtitle.RectCount; index++)
            {
                AVSubtitleRect* rect = rects[index];
                if (rect == null || !IsDrawable(rect))
                {
                    continue;
                }

                // The rectangle's own place inside the cropped image, from its first byte to its last.
                int offset = ((rect->Y - top) * stride) + ((rect->X - left) * 4);
                Span<byte> area = image.Slice(offset, ((rect->H - 1) * stride) + (rect->W * 4));
                DrawRect(rect, area, stride);
            }

            var buffer = new FFmpegSubtitleImageBuffer(pixels, width, height, stride);
            handedOver = true;

            return new SubtitleImage
            {
                Buffer = buffer,
                Left = (double)left / canvasWidth,
                Top = (double)top / canvasHeight,
                Width = (double)width / canvasWidth,
                Height = (double)height / canvasHeight,
            };
        }
        finally
        {
            if (!handedOver)
            {
                Marshal.FreeHGlobal(pixels);
            }
        }
    }

    /// <summary>
    /// How wide the frame the rectangles are placed in is. A bitmap subtitle is positioned inside a plane of its own,
    /// which the decoder learns from the event — 1920 pixels for a track authored at 1080p even when the picture beside
    /// it is 4K — so reading it from the context is what keeps a cue where it was drawn rather than stretching it to
    /// reach the edge of the screen.
    /// </summary>
    private unsafe int ReadCanvasWidth(int fallback)
    {
        int width = _canvasWidth;
        if (width <= 0 && _context != 0)
        {
            width = ((AVCodecContext*)_context)->Width;
        }

        return width > 0 ? Math.Max(width, fallback) : fallback;
    }

    private unsafe int ReadCanvasHeight(int fallback)
    {
        int height = _canvasHeight;
        if (height <= 0 && _context != 0)
        {
            height = ((AVCodecContext*)_context)->Height;
        }

        return height > 0 ? Math.Max(height, fallback) : fallback;
    }

    private static unsafe bool IsDrawable(AVSubtitleRect* rect)
    {
        return rect->Type == FFmpegConstants.SubtitleTypeBitmap && rect->W > 0 && rect->H > 0 && rect->Data[0] != 0;
    }

    /// <summary>
    /// Draws one rectangle into the image. The palette is what tells the two layouts apart: a subtitle that carries
    /// colours is indices into them, and one that carries none is already 32-bit pixels.
    /// </summary>
    private static unsafe void DrawRect(AVSubtitleRect* rect, Span<byte> area, int stride)
    {
        if (rect->ColorCount > 0 && rect->Data[1] != 0)
        {
            int indexStride = rect->LineSize[0] > 0 ? rect->LineSize[0] : rect->W;
            ReadOnlySpan<byte> indices = new((void*)rect->Data[0], indexStride * rect->H);
            ReadOnlySpan<uint> palette = new((void*)rect->Data[1], rect->ColorCount);
            SubtitleBitmap.ExpandIndices(indices, indexStride, palette, rect->W, rect->H, area, stride);
            return;
        }

        int pixelStride = rect->LineSize[0] > 0 ? rect->LineSize[0] : rect->W * 4;
        ReadOnlySpan<byte> pixels = new((void*)rect->Data[0], pixelStride * rect->H);
        SubtitleBitmap.CopyBgra(pixels, pixelStride, rect->W, rect->H, area, stride);
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

    private static unsafe OperationResult<(nint Codec, string Name)> TryFindDecoder(string codecId)
    {
        OperationResultCreator<(nint Codec, string Name)> result = new();

        nint descriptorPointer = FFmpegNative.avcodec_descriptor_get_by_name(codecId);
        if (descriptorPointer == 0)
        {
            return result.Failure($"FFmpeg knows no codec named '{codecId}'.");
        }

        var descriptor = (AVCodecDescriptor*)descriptorPointer;
        if (descriptor->Type != FFmpegConstants.MediaTypeSubtitle)
        {
            return result.Failure($"Codec '{codecId}' is not a subtitle codec.");
        }

        nint codec = FFmpegNative.avcodec_find_decoder(descriptor->Id);
        if (codec == 0)
        {
            return result.Failure($"FFmpeg has no subtitle decoder for '{codecId}'.");
        }

        nint name = FFmpegNative.avcodec_get_name(descriptor->Id);
        return result.Success((codec, name == 0 ? "subtitle" : Marshal.PtrToStringUTF8(name) ?? "subtitle"));
    }
}
