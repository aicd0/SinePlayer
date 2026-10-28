// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
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

    /// <summary>
    /// The graphics API this decoder decodes on, or null when it decodes in software. Everything API-specific is asked
    /// of it, which is what keeps this class free of any one API's vocabulary.
    /// </summary>
    private IFFmpegHardwareBackend? _hardwareBackend;

    private PixelFormat _outputFormat;
    private long _lastPacketTimestamp;
    private bool _hardware;
    private bool _disposed;

    private FFmpegVideoDecoder(FFmpegSource source, VideoStreamInfo stream, nint parameters, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, IFFmpegHardwareBackend? hardwareBackend, int bufferedFrames, string[] candidates)
    {
        _source = source;
        Stream = stream;
        _parameters = parameters;
        _hardwareDecoding = hardwareDecoding;
        _gpuFrames = gpuFrames;
        _gpuDevice = gpuDevice;
        _hardwareBackend = hardwareBackend;
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

    public PixelFormat OutputFormat => _outputFormat;

    public string Name => $"FFmpeg {_decoderName}";

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
    public static OperationResult<FFmpegVideoDecoder> TryCreate(IMediaSource source, VideoStreamInfo stream, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, int bufferedFrames)
    {
        return TryCreate(source, stream, hardwareDecoding, gpuFrames, gpuDevice, bufferedFrames, GetDecoderCandidates(stream.CodecId, hardwareDecoding));
    }

    /// <summary>
    /// Opens a decoder with the candidates named explicitly. This is how a test reaches the fallback for a
    /// decoder that opens and then produces nothing, which is otherwise a property of the FFmpeg build.
    /// </summary>
    internal static OperationResult<FFmpegVideoDecoder> TryCreate(IMediaSource source, VideoStreamInfo stream, bool hardwareDecoding, bool gpuFrames, IGpuDevice? gpuDevice, int bufferedFrames, IReadOnlyList<string> candidates)
    {
        OperationResultCreator<FFmpegVideoDecoder> result = new();

        if (source is not FFmpegSource ffmpegSource)
        {
            return result.Failure("The video stream does not come from an FFmpeg source.");
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

        // The API to decode on is settled once, before anything is opened: a device the pipeline offers names it, and
        // nothing offered means this build picks one for itself.
        IFFmpegHardwareBackend? hardwareBackend = null;
        if (hardwareDecoding)
        {
            OperationResult<IFFmpegHardwareBackend> created = FFmpegHardwareBackends.TryCreate(gpuDevice);
            if (!created.IsSuccessful)
            {
                return result.Failure(created);
            }

            hardwareBackend = created.Result;
        }

        // A layout the renderer cannot sample is refused before anything is opened, which is the only moment the caller
        // can still ask for a decoder that does not keep frames on the GPU — a picture that could never be drawn is
        // better refused once than returned as a failure per frame.
        if (hardwareBackend is not null && gpuFrames && gpuDevice is not null)
        {
            PixelFormat layout = hardwareBackend.GetFrameLayout(stream.PixelFormat);

            // A stream whose pixels this build cannot name is left to the texture: the frame carries its own format,
            // and guessing at one would refuse content that draws perfectly well.
            if (layout != PixelFormat.Unknown)
            {
                OperationResult displayable = gpuDevice.CanDisplayGpuFrame(layout);
                if (!displayable.IsSuccessful)
                {
                    hardwareBackend.Dispose();
                    return result.Failure($"The stream is decoded into {layout} frames: {displayable.Message}");
                }
            }
        }

        var decoder = new FFmpegVideoDecoder(ffmpegSource, stream, parameters, hardwareDecoding, gpuFrames, gpuDevice, hardwareBackend, bufferedFrames, [.. candidates]);
        OperationResult opened = decoder.TryOpenNextCandidate();
        if (!opened.IsSuccessful)
        {
            decoder.Dispose();
            return result.Failure(opened);
        }

        return result.Success(decoder);
    }

    /// <summary>
    /// Opens the next candidate that this codec allows, replacing anything already open. Fails when the candidates are
    /// exhausted, with the last failure as the reason.
    /// </summary>
    private OperationResult TryOpenNextCandidate()
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
            //
            // What is already open is released first: the attachment the next attempt makes is held by the same
            // backend, so closing afterwards would release the new one instead of the old one.
            CloseContext();

            nint context;
            OperationResult<nint> opened = TryOpenContext(name, codec, _parameters, _hardwareBackend, _bufferedFrames);

            if (!opened.IsSuccessful)
            {
                lastError = opened.Message;
                continue;
            }

            context = opened.Result;

            nint receiveFrame = FFmpegNative.av_frame_alloc();
            if (receiveFrame == 0)
            {
                FFmpegNative.avcodec_free_context(ref context);
                _hardwareBackend?.Detach();
                lastError = "Unable to allocate an FFmpeg frame.";
                continue;
            }

            _context = context;
            _receiveFrame = receiveFrame;
            _decoderName = name;
            _hardware = false;
            return OperationResult.Success();
        }

        return OperationResult.Failure(string.IsNullOrEmpty(lastError) ? $"No FFmpeg decoder handles codec '{Stream.CodecId}'." : lastError);
    }

    /// <summary>
    /// Opens a codec context, either on the device a hardware backend decodes with or without one. The context is owned
    /// by the caller once this succeeds, and released here when it does not.
    /// </summary>
    private static OperationResult<nint> TryOpenContext(string name, nint codec, nint parameters, IFFmpegHardwareBackend? hardware, int bufferedFrames)
    {
        OperationResultCreator<nint> result = new();

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

        if (hardware is not null)
        {
            OperationResult attached = hardware.TryAttach(context);
            if (!attached.IsSuccessful)
            {
                FFmpegNative.avcodec_free_context(ref context);
                return result.Failure(attached);
            }
        }

        // A codec context asks for one thread unless it is told otherwise, and one thread is one core:
        // a 4K stream that a frame-threaded decoder plays without a dropped frame cannot be decoded at
        // sixty frames a second on a single core, so the picture falls behind while the processor sits
        // almost idle. Zero means "as many as the machine has", which is what every player that does
        // this well is doing.
        FFmpegNative.av_opt_set_int(context, "threads", DecoderThreadCount, SearchChildren);

        if (hardware is not null)
        {
            // Asked for before the decoder opens, because the pool is built from it: libavcodec allocates
            // extra_hw_frames more surfaces than the codec asks for, which is exactly the room the caller's
            // own buffer needs. Zero leaves the default alone.
            if (bufferedFrames > 0)
            {
                FFmpegNative.av_opt_set_int(context, "extra_hw_frames", bufferedFrames, SearchChildren);
            }
        }

        int opened = FFmpegNative.avcodec_open2(context, codec, 0);
        if (opened < 0)
        {
            FFmpegNative.avcodec_free_context(ref context);
            return result.Failure($"Unable to open the '{name}' decoder: {FFmpegInterop.GetErrorMessage(opened)}");
        }

        return result.Success(context);
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

    public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
    {
        OperationResult result = TryDecodeCore(packet, out frames);
        if (result.IsSuccessful)
        {
            return result;
        }

        // A decoder that opened and then failed before producing a single frame is one this codec cannot
        // use — libavcodec's AV1 decoder has no software path — so the next candidate gets the same packet.
        // Nothing of the stream has been consumed yet, which is what makes handing the packet over safe.
        OperationResult switched = TrySwitchDecoder();
        if (!switched.IsSuccessful)
        {
            return result;
        }

        return TryDecodeCore(packet, out frames);
    }

    private OperationResult TryDecodeCore(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
    {
        List<RawVideoFrame> decoded = [];
        frames = decoded;

        if (_disposed)
        {
            return OperationResult.Failure("The decoder has already been disposed.");
        }

        if (packet.Buffer is not FFmpegPacketBuffer packetBuffer)
        {
            return OperationResult.Failure("The packet was not produced by the FFmpeg backend.");
        }

        _lastPacketTimestamp = packet.Timestamp.Ticks;

        int sendResult = FFmpegNative.avcodec_send_packet(_context, packetBuffer.Packet);
        if (sendResult == FFmpegConstants.ErrorAgain)
        {
            // The decoder wants its pending output collected before it accepts more input. The packet
            // is still ours at this point, so drain and try once more.
            int pending = decoded.Count;
            OperationResult drainResult = ReceiveFrames(decoded);
            if (!drainResult.IsSuccessful)
            {
                return drainResult;
            }

            if (decoded.Count == pending)
            {
                return OperationResult.Failure("The decoder is not accepting input.");
            }

            sendResult = FFmpegNative.avcodec_send_packet(_context, packetBuffer.Packet);
        }

        if (sendResult < 0 && sendResult != FFmpegConstants.ErrorEndOfStream)
        {
            return OperationResult.Failure($"Unable to submit a packet to the decoder: {FFmpegInterop.GetErrorMessage(sendResult)}");
        }

        return ReceiveFrames(decoded);
    }

    /// <summary>
    /// Moves to the next decoder the codec allows. Only safe before a frame was produced: nothing of the
    /// stream has been decoded yet, so the packet that failed is still the first thing the new one sees.
    /// </summary>
    private OperationResult TrySwitchDecoder()
    {
        if (_framesProduced > 0)
        {
            return OperationResult.Failure("A decoder that has already produced a frame cannot be replaced.");
        }

        return TryOpenNextCandidate();
    }

    public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
    {
        List<RawVideoFrame> decoded = [];
        frames = decoded;

        if (_disposed)
        {
            return OperationResult.Failure("The decoder has already been disposed.");
        }

        // A null packet puts the decoder into draining mode.
        int result = FFmpegNative.avcodec_send_packet(_context, 0);
        if (result < 0 && result != FFmpegConstants.ErrorEndOfStream)
        {
            return OperationResult.Failure($"Unable to drain the decoder: {FFmpegInterop.GetErrorMessage(result)}");
        }

        return ReceiveFrames(decoded);
    }

    public OperationResult Flush()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The decoder has already been disposed.");
        }

        FFmpegNative.avcodec_flush_buffers(_context);
        FFmpegNative.av_frame_unref(_receiveFrame);
        return OperationResult.Success();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CloseContext();

        _hardwareBackend?.Dispose();
        _hardwareBackend = null;
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

        // What the attachment holds is reached by callbacks the context owned — the renderer's lock object among them —
        // so it is released only once the context that holds those callbacks is gone.
        _hardwareBackend?.Detach();
    }

    private OperationResult ReceiveFrames(List<RawVideoFrame> frames)
    {
        while (true)
        {
            int result = FFmpegNative.avcodec_receive_frame(_context, _receiveFrame);
            if (result == FFmpegConstants.ErrorAgain || result == FFmpegConstants.ErrorEndOfStream)
            {
                return OperationResult.Success();
            }

            if (result < 0)
            {
                return OperationResult.Failure($"Unable to receive a decoded frame: {FFmpegInterop.GetErrorMessage(result)}");
            }

            OperationResult<RawVideoFrame> taken = TryTakeFrame();
            if (!taken.IsSuccessful)
            {
                return OperationResult.Failure(taken);
            }

            frames.Add(taken.Result);
            _framesProduced++;
        }
    }

    private OperationResult<RawVideoFrame> TryTakeFrame()
    {
        OperationResultCreator<RawVideoFrame> result = new();

        bool hardware = _hardwareBackend is not null && GetFrameFormat(_receiveFrame) == _hardwareBackend.FramePixelFormat;
        if (hardware)
        {
            _hardware = true;

            if (_gpuFrames)
            {
                return TryTakeGpuFrame();
            }
        }

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            return result.Failure("Unable to allocate an FFmpeg frame.");
        }

        if (hardware)
        {
            // A hardware frame holds a texture, which nothing downstream of the decoder can read. The copy
            // into system memory is what turns it into one of the frames the pipeline hands out.
            //
            // The transfer moves pixels and nothing else: the destination carries no timestamp, and a frame
            // without one is placed by the last packet that was submitted, which is not where it belongs. The
            // picture's own timing is carried across by hand for that reason — without it the first frames of
            // every hardware-decoded file are stamped with the wrong moment and the clock anchors to them.
            long sourcePts = GetFrameTimestamp(_receiveFrame);
            int transferred = FFmpegNative.av_hwframe_transfer_data(owned, _receiveFrame, 0);
            FFmpegNative.av_frame_unref(_receiveFrame);
            if (transferred < 0)
            {
                FFmpegNative.av_frame_free(ref owned);
                return result.Failure($"Unable to copy a decoded frame out of the GPU: {FFmpegInterop.GetErrorMessage(transferred)}");
            }

            unsafe
            {
                ((AVFrame*)owned)->Pts = sourcePts;
            }
        }
        else
        {
            // Moves the buffer references across; the receive frame is blank again afterwards.
            FFmpegNative.av_frame_move_ref(owned, _receiveFrame);
        }

        var format = FFmpegInterop.ToPixelFormat(GetFrameFormat(owned));
        _outputFormat = format;

        return result.Success(new RawVideoFrame
        {
            Timestamp = ResolveTimestamp(owned),
            Duration = _frameDuration,
            Width = GetFrameWidth(owned),
            Height = GetFrameHeight(owned),
            Format = format,
            IsHardwareBacked = false,
            Colour = ReadColour(),
            Buffer = new FFmpegVideoFrameBuffer(owned, FFmpegInterop.GetPlaneCount(format)),
        });
    }

    /// <summary>
    /// Hands a hardware frame out as the texture it already is. Nothing is copied: the frame keeps the
    /// reference to the texture libavcodec wrote, and disposing the frame is what gives it back.
    /// </summary>
    private OperationResult<RawVideoFrame> TryTakeGpuFrame()
    {
        OperationResultCreator<RawVideoFrame> result = new();

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            return result.Failure("Unable to allocate an FFmpeg frame.");
        }

        FFmpegNative.av_frame_move_ref(owned, _receiveFrame);

        // The frame's own format is the hardware one; the layout of the pixels inside the texture is what
        // the frames context says it holds.
        if (!FFmpegHardwareFrames.TryReadLayout(_context, out int layout))
        {
            FFmpegNative.av_frame_free(ref owned);
            return result.Failure($"'{Name}' produced a frame with no frames context, so there is no texture layout to report.");
        }

        var format = FFmpegInterop.ToPixelFormat(layout);
        _outputFormat = format;

        return result.Success(new RawVideoFrame
        {
            Timestamp = ResolveTimestamp(owned),
            Duration = _frameDuration,
            Width = GetFrameWidth(owned),
            Height = GetFrameHeight(owned),
            Format = format,
            IsHardwareBacked = true,
            Colour = ReadColour(),
            Buffer = new FFmpegGpuVideoFrameBuffer(owned),
        });
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

    private unsafe ColourInfo ReadColour()
    {
        var context = (AVCodecContext*)_context;
        return context == null ? ColourInfo.Unknown : FFmpegInterop.ToColourInfo(context->ColorPrimaries, context->ColorTransfer, context->ColorRange);
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
