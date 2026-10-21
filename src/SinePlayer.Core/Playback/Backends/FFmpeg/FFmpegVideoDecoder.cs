// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Decodes video with libavcodec, on the GPU when the codec has a hardware decoder for it and on the CPU
/// otherwise. Frames are handed out as plane descriptions rather than copies, so uploading them is the
/// renderer's job.
/// <para>
/// All calls happen on one thread; the decoder holds no locks.
/// </para>
/// </summary>
public sealed partial class FFmpegVideoDecoder : IVideoDecoder
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
    private readonly bool _gpuFrames;
    private readonly bool _hardwareDecoding;
    private readonly IGpuDevice? _gpuDevice;
    private readonly int _bufferedFrames;
    private readonly string[] _candidates;

    private FFmpegSource? _source;
    private nint _parameters;
    private int _candidateIndex;
    private long _framesProduced;
    private string _decoderName;

    private nint _context;
    private nint _receiveFrame;
    private nint _lockHandle;
    private PixelFormat _outputFormat;
    private long _lastPacketTimestamp;
    private bool _hardware;
    private bool _disposed;

    private FFmpegVideoDecoder(FFmpegSource source, VideoStreamInfo stream, nint parameters, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, int bufferedFrames, string[] candidates)
    {
        _source = source;
        Stream = stream;
        _parameters = parameters;
        _hardwareDecoding = hardwareDecoding;
        _gpuFrames = gpuFrames;
        _gpuDevice = gpuDevice;
        _bufferedFrames = bufferedFrames;
        _candidates = candidates;
        _timeBase = source.GetTimeBase(stream.Index);
        _frameDuration = stream.FrameRate > 0 ? TimeSpan.FromSeconds(1 / stream.FrameRate) : TimeSpan.Zero;
        _decoderName = string.Empty;

        // Best effort until the first frame reveals the format the decoder actually chose.
        _outputFormat = stream.PixelFormat;
    }

    public MediaStreamInfo Stream { get; }

    public bool IsHardwareAccelerated => _hardware;

    /// <summary>
    /// The device the hardware frames belong to and the immediate context that can read them back, or
    /// zeroes when decoding never left the CPU. A texture is only readable by the device that decoded it,
    /// so whoever draws or inspects a hardware frame needs these.
    /// </summary>
    internal (nint Device, nint Context) HardwareDevice => FFmpegHardware.ReadDevice(_context);

    public PixelFormat OutputFormat => _outputFormat;

    public string Name => _decoderName;

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
    /// <param name="hardwareDecoding">
    /// Whether decoding must happen on the GPU. A decoder that cannot do it is not opened at all: the caller
    /// asked for hardware and is entitled to a failure rather than frames that were quietly copied back, so
    /// giving that up is the caller's decision to make by asking again without it.
    /// </param>
    /// <param name="gpuFrames">
    /// Whether a hardware frame should be handed out as the texture it already is instead of being copied
    /// into system memory. Nothing can draw a texture yet, so the pipeline leaves this off.
    /// </param>
    /// <param name="gpuDevice">
    /// The device to decode on and the lock that guards it, or null to create a device of its own.
    /// </param>
    /// <param name="bufferedFrames">
    /// How many decoded frames the caller may hold at once. A hardware decoder's frames come from a pool it
    /// owns, and one the caller still holds cannot be reused, so a pool sized without this runs dry while
    /// the caller is doing nothing wrong — which surfaces as a decode failing with "cannot allocate memory".
    /// </param>
    public static bool TryCreate(IMediaSource source, VideoStreamInfo stream, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, int bufferedFrames, out FFmpegVideoDecoder? decoder, out string error)
    {
        return TryCreate(source, stream, hardwareDecoding, gpuFrames, gpuDevice, bufferedFrames, GetDecoderCandidates(stream.CodecId, hardwareDecoding), out decoder, out error);
    }

    /// <summary>
    /// Opens a decoder with the candidates named explicitly. This is how a test reaches the fallback for a
    /// decoder that opens and then produces nothing, which is otherwise a property of the FFmpeg build.
    /// </summary>
    internal static bool TryCreate(IMediaSource source, VideoStreamInfo stream, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, int bufferedFrames, IReadOnlyList<string> candidates, out FFmpegVideoDecoder? decoder, out string error)
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

        var created = new FFmpegVideoDecoder(ffmpegSource, stream, parameters, hardwareDecoding, gpuFrames, gpuDevice, bufferedFrames, [.. candidates]);
        if (created.TryOpenNextCandidate(out error))
        {
            decoder = created;
            return true;
        }

        created.Dispose();
        return false;
    }

    /// <summary>
    /// Opens the next candidate that this codec allows, replacing anything already open. Returns false when
    /// the candidates are exhausted, with the last failure as the reason.
    /// </summary>
    private bool TryOpenNextCandidate(out string error)
    {
        string lastError = string.Empty;
        while (_candidateIndex < _candidates.Length)
        {
            string name = _candidates[_candidateIndex++];
            nint codec = FFmpegNative.avcodec_find_decoder_by_name(name);
            if (codec == 0)
            {
                continue;
            }

            // Hardware only: this decoder is asked for on the GPU when the caller required it and in software
            // when it did not, and it never decides that for itself — giving the requirement up is the
            // pipeline's decision, and it makes it by asking again.
            nint context;
            nint lockHandle = 0;
            bool opened = _hardwareDecoding
                ? TryOpenContext(name, codec, _parameters, hardware: true, _gpuDevice, _bufferedFrames, out context, out lockHandle, out lastError)
                : TryOpenContext(name, codec, _parameters, hardware: false, gpuDevice: null, bufferedFrames: 0, out context, out lockHandle, out lastError);

            if (!opened)
            {
                continue;
            }

            nint receiveFrame = FFmpegNative.av_frame_alloc();
            if (receiveFrame == 0)
            {
                FFmpegNative.avcodec_free_context(ref context);
                FFmpegHardware.FreeLockHandle(lockHandle);
                lastError = "Unable to allocate an FFmpeg frame.";
                continue;
            }

            CloseContext();

            _context = context;
            _receiveFrame = receiveFrame;
            _lockHandle = lockHandle;
            _decoderName = name;
            _hardware = false;
            error = string.Empty;
            return true;
        }

        error = string.IsNullOrEmpty(lastError) ? $"No FFmpeg decoder handles codec '{Stream.CodecId}'." : lastError;
        return false;
    }

    /// <summary>
    /// Opens a codec context, either on the D3D11 device or without one. The context is owned by the
    /// caller once this succeeds, and released here when it does not.
    /// </summary>
    private static bool TryOpenContext(string name, nint codec, nint parameters, bool hardware, IGpuDevice? gpuDevice, int bufferedFrames, out nint context, out nint lockHandle, out string error)
    {
        lockHandle = 0;
        context = FFmpegNative.avcodec_alloc_context3(codec);
        if (context == 0)
        {
            error = "Unable to allocate an FFmpeg codec context.";
            return false;
        }

        int result = FFmpegNative.avcodec_parameters_to_context(context, parameters);
        if (result < 0)
        {
            error = $"Unable to apply the stream's codec parameters: {FFmpegInterop.GetErrorMessage(result)}";
            FFmpegNative.avcodec_free_context(ref context);
            return false;
        }

        if (hardware && !FFmpegHardware.TryAttachDevice(context, gpuDevice?.NativeDevice ?? 0, gpuDevice?.DeviceLock, out lockHandle, out error))
        {
            FFmpegNative.avcodec_free_context(ref context);
            return false;
        }

        // A codec context asks for one thread unless it is told otherwise, and one thread is one core:
        // a 4K stream that a frame-threaded decoder plays without a dropped frame cannot be decoded at
        // sixty frames a second on a single core, so the picture falls behind while the processor sits
        // almost idle. Zero means "as many as the machine has", which is what every player that does
        // this well is doing.
        FFmpegNative.av_opt_set_int(context, "threads", DecoderThreadCount, SearchChildren);

        if (hardware)
        {
            // Asked for before the decoder opens, because the pool is built from it: libavcodec allocates
            // extra_hw_frames more surfaces than the codec asks for, which is exactly the room the caller's
            // own buffer needs. Zero leaves the default alone.
            if (bufferedFrames > 0)
            {
                FFmpegNative.av_opt_set_int(context, "extra_hw_frames", bufferedFrames, SearchChildren);
            }
        }

        result = FFmpegNative.avcodec_open2(context, codec, 0);
        if (result < 0)
        {
            error = $"Unable to open the '{name}' decoder: {FFmpegInterop.GetErrorMessage(result)}";
            FFmpegNative.avcodec_free_context(ref context);
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Decoder names to try, in order. FFmpeg's own AV1 decoder is the only hardware path for that codec and
    /// has no software one at all — it fails on the first packet when it cannot decode on a GPU — so it is
    /// tried first when hardware is wanted and libdav1d is the fallback that produces the frames. Every other
    /// codec has a single decoder that covers both paths.
    /// </summary>
    private static string[] GetDecoderCandidates(string codecId, bool hardwareDecoding)
    {
        if (!string.Equals(codecId, "av1", StringComparison.OrdinalIgnoreCase))
        {
            return [codecId];
        }

        return hardwareDecoding ? [codecId, "libdav1d"] : ["libdav1d", codecId];
    }

    public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
    {
        PlaybackOperationResult result = TryDecodeCore(packet, out frames);
        if (result.IsSuccessful)
        {
            return result;
        }

        // A decoder that opened and then failed before producing a single frame is one this codec cannot
        // use — libavcodec's AV1 decoder has no software path — so the next candidate gets the same packet.
        // Nothing of the stream has been consumed yet, which is what makes handing the packet over safe.
        if (!TrySwitchDecoder())
        {
            return result;
        }

        return TryDecodeCore(packet, out frames);
    }

    private PlaybackOperationResult TryDecodeCore(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
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

    /// <summary>
    /// Moves to the next decoder the codec allows. Only safe before a frame was produced: nothing of the
    /// stream has been decoded yet, so the packet that failed is still the first thing the new one sees.
    /// </summary>
    private bool TrySwitchDecoder()
    {
        if (_framesProduced > 0)
        {
            return false;
        }

        return TryOpenNextCandidate(out _);
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

        CloseContext();

        _source = null;
        _parameters = 0;
    }

    /// <summary>
    /// Releases the open decoder and everything that belongs to it: the frame, the device lock, and the codec
    /// context that owns both. The decoder itself stays usable, so a fallback can open the next candidate.
    /// </summary>
    private void CloseContext()
    {
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

        // The lock handle keeps the renderer's lock object alive for as long as libavcodec could call it, so
        // it is released only once the context that holds those callbacks is gone.
        nint lockHandle = _lockHandle;
        _lockHandle = 0;
        FFmpegHardware.FreeLockHandle(lockHandle);
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

            if (!TryTakeFrame(out RawVideoFrame? frame, out string frameError) || frame is null)
            {
                return PlaybackOperationResult.Failure(frameError);
            }

            frames.Add(frame);
            _framesProduced++;
        }
    }

    private bool TryTakeFrame(out RawVideoFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;

        bool hardware = GetFrameFormat(_receiveFrame) == FFmpegHardware.PixelFormatD3D11;
        if (hardware)
        {
            _hardware = true;

            if (_gpuFrames)
            {
                return TryTakeGpuFrame(out frame, out error);
            }
        }

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            error = "Unable to allocate an FFmpeg frame.";
            return false;
        }

        if (hardware)
        {
            // A hardware frame holds a texture, which nothing downstream of the decoder can read. The copy
            // into system memory is what turns it into one of the frames the pipeline hands out.
            int result = FFmpegNative.av_hwframe_transfer_data(owned, _receiveFrame, 0);
            FFmpegNative.av_frame_unref(_receiveFrame);
            if (result < 0)
            {
                FFmpegNative.av_frame_free(ref owned);
                error = $"Unable to copy a decoded frame out of the GPU: {FFmpegInterop.GetErrorMessage(result)}";
                return false;
            }
        }
        else
        {
            // Moves the buffer references across; the receive frame is blank again afterwards.
            FFmpegNative.av_frame_move_ref(owned, _receiveFrame);
        }

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

    /// <summary>
    /// Hands a hardware frame out as the texture it already is. Nothing is copied: the frame keeps the
    /// reference to the texture libavcodec wrote, and disposing the frame is what gives it back.
    /// </summary>
    private bool TryTakeGpuFrame(out RawVideoFrame? frame, out string error)
    {
        frame = null;
        error = string.Empty;

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            error = "Unable to allocate an FFmpeg frame.";
            return false;
        }

        FFmpegNative.av_frame_move_ref(owned, _receiveFrame);

        // The frame's own format is the hardware one; the layout of the pixels inside the texture is what
        // the frames context says it holds.
        var format = FFmpegInterop.ToPixelFormat(FFmpegHardware.ReadHardwareFrameFormat(_context));
        _outputFormat = format;

        frame = new RawVideoFrame
        {
            Timestamp = ResolveTimestamp(owned),
            Duration = _frameDuration,
            Width = GetFrameWidth(owned),
            Height = GetFrameHeight(owned),
            Format = format,
            IsHardwareBacked = true,
            Buffer = new FFmpegGpuVideoFrameBuffer(owned),
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
