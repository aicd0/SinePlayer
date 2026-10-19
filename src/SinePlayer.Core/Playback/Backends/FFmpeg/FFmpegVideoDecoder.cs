// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Decodes video on the CPU with libavcodec. Frames are handed out as plane descriptions rather than
/// copies, so uploading them is the renderer's job.
/// <para>
/// All calls happen on one thread; the decoder holds no locks.
/// </para>
/// </summary>
public sealed class FFmpegVideoDecoder : IVideoDecoder
{
    /// <summary><c>AV_OPT_SEARCH_CHILDREN</c>: look at the codec's own options, not just the context's.</summary>
    private const int SearchChildren = 1;

    /// <summary>
    /// How many threads a decoder may use. Enough for a 4K stream to be decoded several times faster than
    /// it plays, and no more: a frame-threaded decoder holds its output back by roughly one frame per
    /// thread, so handing it every processor on the machine means a seek that lands near the end of a file
    /// leaves it with less than it needs to fill that pipeline, and the picture does not appear.
    /// </summary>
    private static readonly int DecoderThreadCount = Math.Clamp(Environment.ProcessorCount, 1, 8);

    private readonly AVRational _timeBase;
    private readonly TimeSpan _frameDuration;

    private nint _context;
    private nint _receiveFrame;
    private PixelFormat _outputFormat;
    private long _lastPacketTimestamp;
    private bool _disposed;

    private FFmpegVideoDecoder(VideoStreamInfo stream, nint context, nint receiveFrame, AVRational timeBase, TimeSpan frameDuration)
    {
        Stream = stream;
        _context = context;
        _receiveFrame = receiveFrame;
        _timeBase = timeBase;
        _frameDuration = frameDuration;

        // Best effort until the first frame reveals the format the decoder actually chose.
        _outputFormat = stream.PixelFormat;
    }

    public MediaStreamInfo Stream { get; }

    public bool IsHardwareAccelerated => false;

    public PixelFormat OutputFormat => _outputFormat;

    /// <summary>
    /// The thread setting the decoder was opened with: zero means "as many as the machine has", which is
    /// what a decoder left alone does not do — the default is one thread. Exposed so that a test can hold
    /// the setting in place, because losing it again would be silent: the picture falls behind while the
    /// processor sits almost idle.
    /// </summary>
    internal int ThreadCount
    {
        get
        {
            nint context = _context;
            if (context == 0 || FFmpegNative.av_opt_get_int(context, "threads", SearchChildren, out long threads) < 0)
            {
                return 0;
            }

            return (int)threads;
        }
    }

    /// <summary>
    /// Creates and opens a decoder for a video stream of an FFmpeg source.
    /// </summary>
    public static bool TryCreate(IMediaSource source, VideoStreamInfo stream, out FFmpegVideoDecoder? decoder, out string error)
    {
        decoder = null;

        if (source is not FFmpegSource ffmpegSource)
        {
            error = "The video stream does not come from an FFmpeg source.";
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

        string lastError = string.Empty;
        foreach (string name in GetDecoderCandidates(stream.CodecId))
        {
            nint codec = FFmpegNative.avcodec_find_decoder_by_name(name);
            if (codec == 0)
            {
                continue;
            }

            nint context = FFmpegNative.avcodec_alloc_context3(codec);
            if (context == 0)
            {
                lastError = "Unable to allocate an FFmpeg codec context.";
                continue;
            }

            int result = FFmpegNative.avcodec_parameters_to_context(context, parameters);
            if (result < 0)
            {
                FFmpegNative.avcodec_free_context(ref context);
                lastError = $"Unable to apply the stream's codec parameters: {FFmpegInterop.GetErrorMessage(result)}";
                continue;
            }

            // A codec context asks for one thread unless it is told otherwise, and one thread is one core:
            // a 4K stream that a frame-threaded decoder plays without a dropped frame cannot be decoded at
            // sixty frames a second on a single core, so the picture falls behind while the processor sits
            // almost idle. Zero means "as many as the machine has", which is what every player that does
            // this well is doing.
            FFmpegNative.av_opt_set_int(context, "threads", DecoderThreadCount, SearchChildren);

            result = FFmpegNative.avcodec_open2(context, codec, 0);
            if (result < 0)
            {
                FFmpegNative.avcodec_free_context(ref context);
                lastError = $"Unable to open the '{name}' decoder: {FFmpegInterop.GetErrorMessage(result)}";
                continue;
            }

            nint receiveFrame = FFmpegNative.av_frame_alloc();
            if (receiveFrame == 0)
            {
                FFmpegNative.avcodec_free_context(ref context);
                lastError = "Unable to allocate an FFmpeg frame.";
                continue;
            }

            TimeSpan frameDuration = stream.FrameRate > 0 ? TimeSpan.FromSeconds(1 / stream.FrameRate) : TimeSpan.Zero;
            decoder = new FFmpegVideoDecoder(stream, context, receiveFrame, ffmpegSource.GetTimeBase(stream.Index), frameDuration);
            error = string.Empty;
            return true;
        }

        error = string.IsNullOrEmpty(lastError) ? $"No FFmpeg decoder handles codec '{stream.CodecId}'." : lastError;
        return false;
    }

    /// <summary>
    /// Decoder names to try, in order. FFmpeg's default decoder for a codec is not always the one that
    /// works: the built-in AV1 decoder only decodes through a hardware path on this platform, so the
    /// dedicated software decoder is preferred and the built-in one stays as the fallback. Hardware
    /// decoding will be requested explicitly rather than left to this ordering.
    /// </summary>
    private static IEnumerable<string> GetDecoderCandidates(string codecId)
    {
        if (string.Equals(codecId, "av1", StringComparison.OrdinalIgnoreCase))
        {
            yield return "libdav1d";
        }

        yield return codecId;
    }

    public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
    {
        List<RawVideoFrame> decoded = [];
        frames = decoded;

        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The decoder has already been disposed.");
        }

        if (packet.Buffer is not FFmpegPacketBuffer packetBuffer)
        {
            return PlaybackOperationResult.Failure("The packet was not produced by the FFmpeg backend.");
        }

        _lastPacketTimestamp = packet.Timestamp.Ticks;

        int sendResult = FFmpegNative.avcodec_send_packet(_context, packetBuffer.Packet);
        if (sendResult == FFmpegConstants.ErrorAgain)
        {
            // The decoder wants its pending output collected before it accepts more input. The packet
            // is still ours at this point, so drain and try once more.
            int pending = decoded.Count;
            PlaybackOperationResult drainResult = ReceiveFrames(decoded);
            if (!drainResult.IsSuccessful)
            {
                return drainResult;
            }

            if (decoded.Count == pending)
            {
                return PlaybackOperationResult.Failure("The decoder is not accepting input.");
            }

            sendResult = FFmpegNative.avcodec_send_packet(_context, packetBuffer.Packet);
        }

        if (sendResult < 0 && sendResult != FFmpegConstants.ErrorEndOfStream)
        {
            return PlaybackOperationResult.Failure($"Unable to submit a packet to the decoder: {FFmpegInterop.GetErrorMessage(sendResult)}");
        }

        return ReceiveFrames(decoded);
    }

    public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
    {
        List<RawVideoFrame> decoded = [];
        frames = decoded;

        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The decoder has already been disposed.");
        }

        // A null packet puts the decoder into draining mode.
        int result = FFmpegNative.avcodec_send_packet(_context, 0);
        if (result < 0 && result != FFmpegConstants.ErrorEndOfStream)
        {
            return PlaybackOperationResult.Failure($"Unable to drain the decoder: {FFmpegInterop.GetErrorMessage(result)}");
        }

        return ReceiveFrames(decoded);
    }

    public PlaybackOperationResult Flush()
    {
        if (_disposed)
        {
            return PlaybackOperationResult.Failure("The decoder has already been disposed.");
        }

        FFmpegNative.avcodec_flush_buffers(_context);
        FFmpegNative.av_frame_unref(_receiveFrame);
        return PlaybackOperationResult.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        nint receiveFrame = _receiveFrame;
        _receiveFrame = 0;
        if (receiveFrame != 0)
        {
            FFmpegNative.av_frame_free(ref receiveFrame);
        }

        nint context = _context;
        _context = 0;
        if (context != 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
        }
    }

    private PlaybackOperationResult ReceiveFrames(List<RawVideoFrame> frames)
    {
        while (true)
        {
            int result = FFmpegNative.avcodec_receive_frame(_context, _receiveFrame);
            if (result == FFmpegConstants.ErrorAgain || result == FFmpegConstants.ErrorEndOfStream)
            {
                return PlaybackOperationResult.Success;
            }

            if (result < 0)
            {
                return PlaybackOperationResult.Failure($"Unable to receive a decoded frame: {FFmpegInterop.GetErrorMessage(result)}");
            }

            if (!TryTakeFrame(out RawVideoFrame? frame) || frame is null)
            {
                return PlaybackOperationResult.Failure("Unable to allocate an FFmpeg frame.");
            }

            frames.Add(frame);
        }
    }

    private bool TryTakeFrame(out RawVideoFrame? frame)
    {
        frame = null;

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            return false;
        }

        // Moves the buffer references across; the receive frame is blank again afterwards.
        FFmpegNative.av_frame_move_ref(owned, _receiveFrame);

        var format = FFmpegInterop.ToPixelFormat(GetFrameFormat(owned));
        _outputFormat = format;

        frame = new RawVideoFrame
        {
            Timestamp = ResolveTimestamp(owned),
            Duration = _frameDuration,
            Width = GetFrameWidth(owned),
            Height = GetFrameHeight(owned),
            Format = format,
            IsHardwareBacked = false,
            Buffer = new FFmpegVideoFrameBuffer(owned, FFmpegInterop.GetPlaneCount(format)),
        };
        return true;
    }

    private TimeSpan ResolveTimestamp(nint frame)
    {
        long timestamp = GetFrameTimestamp(frame);
        if (timestamp == FFmpegConstants.NoPtsValue)
        {
            // Some demuxers leave the frame without a timestamp; the packet it came from is the best
            // remaining source of truth.
            return _lastPacketTimestamp > 0 ? TimeSpan.FromTicks(_lastPacketTimestamp) : TimeSpan.Zero;
        }

        return FFmpegInterop.ToTimeSpan(timestamp, _timeBase);
    }

    private static unsafe int GetFrameFormat(nint frame)
    {
        return ((AVFrame*)frame)->Format;
    }

    private static unsafe int GetFrameWidth(nint frame)
    {
        return ((AVFrame*)frame)->Width;
    }

    private static unsafe int GetFrameHeight(nint frame)
    {
        return ((AVFrame*)frame)->Height;
    }

    private static unsafe long GetFrameTimestamp(nint frame)
    {
        return ((AVFrame*)frame)->Pts;
    }
}
