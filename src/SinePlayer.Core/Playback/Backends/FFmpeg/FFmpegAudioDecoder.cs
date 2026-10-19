// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Decodes audio on the CPU with libavcodec. Sample rate, channel count and channel layout come from
/// the stream: a decoder does not resample, so those cannot change. Only the sample format is read
/// back from the frames, because that is the one thing a codec is free to choose differently from what
/// the container declares.
/// <para>
/// All calls happen on one thread; the decoder holds no locks.
/// </para>
/// </summary>
public sealed class FFmpegAudioDecoder : IAudioDecoder
{
    private readonly AudioStreamInfo _stream;
    private readonly AVRational _timeBase;

    private nint _context;
    private nint _receiveFrame;
    private AudioSampleFormat _outputFormat;
    private long _lastPacketTimestamp;
    private bool _disposed;

    private FFmpegAudioDecoder(AudioStreamInfo stream, nint context, nint receiveFrame, AVRational timeBase)
    {
        _stream = stream;
        _context = context;
        _receiveFrame = receiveFrame;
        _timeBase = timeBase;

        // Best effort until the first buffer reveals the format the decoder actually chose.
        _outputFormat = stream.SampleFormat;
    }

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    public AudioSampleFormat OutputFormat => _outputFormat;

    /// <summary>
    /// Creates and opens a decoder for an audio stream of an FFmpeg source.
    /// </summary>
    public static bool TryCreate(IMediaSource source, AudioStreamInfo stream, out FFmpegAudioDecoder? decoder, out string error)
    {
        decoder = null;

        if (source is not FFmpegSource ffmpegSource)
        {
            error = "The audio stream does not come from an FFmpeg source.";
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

            decoder = new FFmpegAudioDecoder(stream, context, receiveFrame, ffmpegSource.GetTimeBase(stream.Index));
            error = string.Empty;
            return true;
        }

        error = string.IsNullOrEmpty(lastError) ? $"No FFmpeg decoder handles codec '{stream.CodecId}'." : lastError;
        return false;
    }

    public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
    {
        List<RawAudioBuffer> decoded = [];
        buffers = decoded;

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
            PlaybackOperationResult drainResult = ReceiveBuffers(decoded);
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

        return ReceiveBuffers(decoded);
    }

    public PlaybackOperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
    {
        List<RawAudioBuffer> decoded = [];
        buffers = decoded;

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

        return ReceiveBuffers(decoded);
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

    private PlaybackOperationResult ReceiveBuffers(List<RawAudioBuffer> buffers)
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
                return PlaybackOperationResult.Failure($"Unable to receive decoded audio: {FFmpegInterop.GetErrorMessage(result)}");
            }

            if (!TryTakeBuffer(out RawAudioBuffer? buffer, out string error))
            {
                return PlaybackOperationResult.Failure(error);
            }

            buffers.Add(buffer!);
        }
    }

    private bool TryTakeBuffer(out RawAudioBuffer? buffer, out string error)
    {
        buffer = null;
        error = string.Empty;

        nint owned = FFmpegNative.av_frame_alloc();
        if (owned == 0)
        {
            error = "Unable to allocate an FFmpeg frame.";
            return false;
        }

        // Moves the buffer references across; the receive frame is blank again afterwards.
        FFmpegNative.av_frame_move_ref(owned, _receiveFrame);

        int format = GetFrameFormat(owned);
        int bytesPerSample = FFmpegNative.av_get_bytes_per_sample(format);
        AudioSampleFormat sampleFormat = FFmpegInterop.ToSampleFormat(format);
        if (bytesPerSample <= 0 || sampleFormat == AudioSampleFormat.Unknown)
        {
            FFmpegNative.av_frame_free(ref owned);
            error = $"The decoder produced an unsupported sample format (FFmpeg format {format}).";
            return false;
        }

        int sampleCount = GetFrameSampleCount(owned);
        int channelCount = _stream.ChannelCount;
        bool isPlanar = FFmpegNative.av_sample_fmt_is_planar(format) != 0;
        _outputFormat = sampleFormat;

        // A planar block keeps one channel per plane, an interleaved block keeps them all in one.
        int planeCount = isPlanar ? channelCount : 1;
        int planeByteCount = isPlanar
            ? bytesPerSample * sampleCount
            : bytesPerSample * sampleCount * channelCount;

        buffer = new RawAudioBuffer
        {
            Timestamp = ResolveTimestamp(owned),
            SampleRate = _stream.SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = _stream.ChannelLayout,
            SampleFormat = sampleFormat,
            SampleCount = sampleCount,
            Buffer = new FFmpegAudioFrameBuffer(owned, planeCount, planeByteCount),
        };
        return true;
    }

    private TimeSpan ResolveTimestamp(nint frame)
    {
        long timestamp = GetFrameTimestamp(frame);
        if (timestamp == FFmpegConstants.NoPtsValue)
        {
            // The first packets of a stream often carry no timestamp; the packet's own one is the
            // closest thing to the truth that is available.
            return new TimeSpan(_lastPacketTimestamp);
        }

        return FFmpegInterop.ToTimeSpan(timestamp, _timeBase);
    }

    private static unsafe int GetFrameFormat(nint frame)
    {
        return ((AVFrame*)frame)->Format;
    }

    private static unsafe int GetFrameSampleCount(nint frame)
    {
        return ((AVFrame*)frame)->SampleCount;
    }

    private static unsafe long GetFrameTimestamp(nint frame)
    {
        return ((AVFrame*)frame)->Pts;
    }

    /// <summary>
    /// Decoder names to try, in order. Video needs this because FFmpeg's default decoder for a codec
    /// is not always the one that works; audio keeps the same shape so an ordered preference can be
    /// added here without changing the caller, and so both decoders read alike.
    /// </summary>
    private static IEnumerable<string> GetDecoderCandidates(string codecId)
    {
        yield return codecId;
    }
}
