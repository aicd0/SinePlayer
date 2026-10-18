// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;

using NUnit.Framework;

using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Writes a video file that has sound in it as well, which the AV1 fixture cannot do and which three
/// separate defects have needed: a cycle that needs both streams cannot be reproduced with only one of them,
/// so those had to be reasoned out from thread dumps instead of measured.
/// <para>
/// Nothing is compressed — uncompressed frames and PCM in Matroska — because the point is the shape of the
/// stream, not its contents. Nothing is encoded either: the packets are written by hand, so the fixture needs
/// no encoder that a given FFmpeg build might lack, costs nothing to generate, and says exactly what it
/// contains. Every frame is a keyframe, so a seek lands exactly where it asks.
/// </para>
/// </summary>
internal static partial class RawAudioVideoFixtureFile
{
    private const int Width = 320;
    private const int Height = 180;
    private const int FrameRate = 30;
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int SamplesPerAudioPacket = 1024;
    private const int PixelFormatYuv420p = 0;
    private const int SampleFormatS16 = 1;
    private const int MediaTypeVideo = 0;
    private const int MediaTypeAudio = 1;
    private const int ChannelOrderNative = 1;
    private const int StereoChannelMask = 3;
    private const int PacketFlagKey = 1;
    private const int AvioFlagWrite = 2;

    [LibraryImport(FFmpegNative.AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int avformat_alloc_output_context2(out nint context, nint outputFormat, string formatName, string fileName);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial nint avformat_new_stream(nint context, nint codec);

    [LibraryImport(FFmpegNative.AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int avio_open(out nint context, string url, int flags);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial int avformat_write_header(nint context, nint options);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial int av_write_frame(nint context, nint packet);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial int av_write_trailer(nint context);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial void avio_closep(ref nint context);

    [LibraryImport(FFmpegNative.AvFormat)]
    private static partial void avformat_free_context(nint context);

    [LibraryImport(FFmpegNative.AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint avcodec_find_encoder_by_name(string name);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int av_new_packet(nint packet, int size);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int avcodec_parameters_from_context(nint parameters, nint context);

    [LibraryImport(FFmpegNative.AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int av_opt_set(nint obj, string name, string value, int flags);

    /// <param name="frameCount">How many video frames the file holds. The packet queue holds 64, so a file
    /// that is meant to run the reader into it needs more than that.</param>
    /// <param name="videoFramesAfterTheSound">How many of those frames come after the last sample of audio, which
    /// is the ordinary shape of a real file: the container's duration is the longest stream's, so the picture
    /// usually ends a frame or two after the sound, and in an edited or damaged file it can end much later.</param>
    /// <param name="soundSecondsAfterThePicture">How much sound comes after the last video frame, which is the same
    /// shape the other way round: a sound that runs on when the picture has ended.</param>
    internal static string Create(int frameCount = 200, int videoFramesAfterTheSound = 0, int soundSecondsAfterThePicture = 0)
    {
        FFmpegRuntime.EnsureInitialized();

        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-av-fixture-{Guid.NewGuid():N}.mkv");
        int allocResult = avformat_alloc_output_context2(out nint muxer, 0, "matroska", path);
        Assert.That(allocResult, Is.GreaterThanOrEqualTo(0), $"muxer: {FFmpegInterop.GetErrorMessage(allocResult)}");

        nint io = 0;
        nint videoContext = 0;
        nint audioContext = 0;
        try
        {
            videoContext = OpenVideoCodec();
            audioContext = OpenAudioCodec();

            nint videoStream = AddStream(muxer, videoContext);
            nint audioStream = AddStream(muxer, audioContext);

            int ioResult = avio_open(out io, path, AvioFlagWrite);
            Assert.That(ioResult, Is.GreaterThanOrEqualTo(0), $"io: {FFmpegInterop.GetErrorMessage(ioResult)}");
            Marshal.WriteIntPtr(muxer, (int)Marshal.OffsetOf<AVFormatContext>(nameof(AVFormatContext.IoContext)), io);

            int headerResult = avformat_write_header(muxer, 0);
            Assert.That(headerResult, Is.GreaterThanOrEqualTo(0), $"header: {FFmpegInterop.GetErrorMessage(headerResult)}");

            // The muxer decides the stream time bases, so timestamps are converted into them rather than
            // assumed.
            AVStream videoLayout = Marshal.PtrToStructure<AVStream>(videoStream);
            AVStream audioLayout = Marshal.PtrToStructure<AVStream>(audioStream);

            // Interleaved the way a real file is: the sound sits next to the picture it belongs with, not
            // heaped at one end where the reader would reach it long after it was needed.
            byte[] pixels = new byte[Width * Height * 3 / 2];
            byte[] samples = new byte[SamplesPerAudioPacket * Channels * sizeof(short)];
            int samplesWritten = 0;
            int framesWithSound = Math.Max(1, frameCount - Math.Clamp(videoFramesAfterTheSound, 0, frameCount - 1));
            for (int index = 0; index < frameCount; index++)
            {
                FillPixels(pixels, index);
                WritePacket(muxer, videoLayout, pixels, pixels.Length, index, 1, FrameRate, PacketFlagKey);

                if (index >= framesWithSound)
                {
                    continue;
                }

                int wanted = SampleRate * (index + 1) / FrameRate;
                while (samplesWritten < wanted)
                {
                    int count = Math.Min(SamplesPerAudioPacket, wanted - samplesWritten);
                    FillSamples(samples, count, samplesWritten);
                    WritePacket(muxer, audioLayout, samples, count * Channels * sizeof(short), samplesWritten, count, SampleRate, 0);
                    samplesWritten += count;
                }
            }

            // The sound running on past the last frame: written after the picture has ended, which is where a real
            // file puts it too — the streams are interleaved until one of them runs out.
            int totalSamples = SampleRate * framesWithSound / FrameRate + SampleRate * Math.Max(0, soundSecondsAfterThePicture);
            while (samplesWritten < totalSamples)
            {
                int count = Math.Min(SamplesPerAudioPacket, totalSamples - samplesWritten);
                FillSamples(samples, count, samplesWritten);
                WritePacket(muxer, audioLayout, samples, count * Channels * sizeof(short), samplesWritten, count, SampleRate, 0);
                samplesWritten += count;
            }

            int trailerResult = av_write_trailer(muxer);
            Assert.That(trailerResult, Is.GreaterThanOrEqualTo(0), $"trailer: {FFmpegInterop.GetErrorMessage(trailerResult)}");
        }
        finally
        {
            if (videoContext != 0)
            {
                FFmpegNative.avcodec_free_context(ref videoContext);
            }

            if (audioContext != 0)
            {
                FFmpegNative.avcodec_free_context(ref audioContext);
            }

            if (io != 0)
            {
                avio_closep(ref io);
            }

            avformat_free_context(muxer);
        }

        return path;
    }

    /// <summary>
    /// Opens the codec so that the stream can take its parameters from it. Nothing is encoded with it: the
    /// parameters are what the muxer needs — pixel format, sample format, block size, channel layout — and
    /// getting them from the codec means they are the ones it would have written itself.
    /// </summary>
    private static nint OpenVideoCodec()
    {
        nint codec = avcodec_find_encoder_by_name("rawvideo");
        Assert.That(codec, Is.Not.EqualTo(0), "This FFmpeg build has no rawvideo codec.");

        var parameters = new AVCodecParameters
        {
            CodecType = MediaTypeVideo,
            CodecId = Marshal.ReadInt32(codec, (IntPtr.Size * 2) + sizeof(int)),
            Format = PixelFormatYuv420p,
            Width = Width,
            Height = Height,
            SampleAspectRatio = new AVRational { Numerator = 1, Denominator = 1 },
            FrameRate = new AVRational { Numerator = FrameRate, Denominator = 1 },
        };

        return OpenCodec(codec, parameters, $"1/{FrameRate}");
    }

    private static nint OpenAudioCodec()
    {
        nint codec = avcodec_find_encoder_by_name("pcm_s16le");
        Assert.That(codec, Is.Not.EqualTo(0), "This FFmpeg build has no pcm_s16le codec.");

        var parameters = new AVCodecParameters
        {
            CodecType = MediaTypeAudio,
            CodecId = Marshal.ReadInt32(codec, (IntPtr.Size * 2) + sizeof(int)),
            Format = SampleFormatS16,
            SampleRate = SampleRate,
            ChannelLayout = new AVChannelLayout { Order = ChannelOrderNative, ChannelCount = Channels, Layout = StereoChannelMask },
        };

        return OpenCodec(codec, parameters, $"1/{SampleRate}");
    }

    private static nint OpenCodec(nint codec, AVCodecParameters parameters, string timeBase)
    {
        nint context = FFmpegNative.avcodec_alloc_context3(codec);
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AVCodecParameters>());
        try
        {
            Marshal.StructureToPtr(parameters, buffer, false);
            int geometryResult = FFmpegNative.avcodec_parameters_to_context(context, buffer);
            Assert.That(geometryResult, Is.GreaterThanOrEqualTo(0), $"geometry: {FFmpegInterop.GetErrorMessage(geometryResult)}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // An encoder will not open without one, even when nothing is going to be encoded with it.
        int timeBaseResult = av_opt_set(context, "time_base", timeBase, 0);
        Assert.That(timeBaseResult, Is.GreaterThanOrEqualTo(0), $"time base: {FFmpegInterop.GetErrorMessage(timeBaseResult)}");

        int openResult = FFmpegNative.avcodec_open2(context, codec, 0);
        Assert.That(openResult, Is.GreaterThanOrEqualTo(0), $"open: {FFmpegInterop.GetErrorMessage(openResult)}");
        return context;
    }

    private static nint AddStream(nint muxer, nint codecContext)
    {
        nint stream = avformat_new_stream(muxer, 0);
        Assert.That(stream, Is.Not.EqualTo(0), "no stream");

        nint codecParameters = Marshal.PtrToStructure<AVStream>(stream).CodecParameters;
        int result = avcodec_parameters_from_context(codecParameters, codecContext);
        Assert.That(result, Is.GreaterThanOrEqualTo(0), $"stream parameters: {FFmpegInterop.GetErrorMessage(result)}");
        return stream;
    }

    private static nint AddStream(nint muxer, AVCodecParameters parameters)
    {
        nint stream = avformat_new_stream(muxer, 0);
        Assert.That(stream, Is.Not.EqualTo(0), "no stream");

        // The stream's parameters were allocated by the muxer and are filled in rather than replaced: this is
        // the copy the muxer keeps, and every packet written is matched against it.
        nint codecParameters = Marshal.PtrToStructure<AVStream>(stream).CodecParameters;
        Marshal.StructureToPtr(parameters, codecParameters, false);
        return stream;
    }

    private static void WritePacket(nint muxer, AVStream stream, byte[] content, int length, long timestamp, long duration, int encoderRate, int flags)
    {
        nint packet = FFmpegNative.av_packet_alloc();
        try
        {
            int allocateResult = av_new_packet(packet, length);
            Assert.That(allocateResult, Is.GreaterThanOrEqualTo(0), $"packet: {FFmpegInterop.GetErrorMessage(allocateResult)}");

            nint data = Marshal.ReadIntPtr(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.Data)));
            Marshal.Copy(content, 0, data, length);

            // From 1/encoderRate into the time base the muxer chose for the stream, which is not the one the
            // content was generated in. The packet says which base its timestamps are in: a hand-built packet
            // has none, and the muxer rejects one it cannot place.
            long scaled = timestamp * stream.TimeBase.Denominator / (encoderRate * (long)stream.TimeBase.Numerator);
            long scaledDuration = duration * stream.TimeBase.Denominator / (encoderRate * (long)stream.TimeBase.Numerator);
            Marshal.WriteInt64(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.Pts)), scaled);
            Marshal.WriteInt64(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.Dts)), scaled);
            Marshal.WriteInt64(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.Duration)), scaledDuration);
            Marshal.WriteInt32(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.StreamIndex)), stream.Index);
            Marshal.WriteInt32(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.Flags)), flags);
            Marshal.WriteInt32(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.TimeBase)), stream.TimeBase.Numerator);
            Marshal.WriteInt32(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.TimeBase)) + sizeof(int), stream.TimeBase.Denominator);

            int writeResult = av_write_frame(muxer, packet);
            Assert.That(writeResult, Is.GreaterThanOrEqualTo(0), $"write stream {stream.Index} timebase {stream.TimeBase.Numerator}/{stream.TimeBase.Denominator} pts {scaled} size {length}: {FFmpegInterop.GetErrorMessage(writeResult)}");
        }
        finally
        {
            FFmpegNative.av_packet_free(ref packet);
        }
    }

    private static void FillPixels(byte[] pixels, int index)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                pixels[(y * Width) + x] = (byte)((x + y + (index * 8)) & 0xFF);
            }
        }

        int lumaSize = Width * Height;
        int chromaStride = Width / 2;
        for (int plane = 0; plane < 2; plane++)
        {
            int offset = lumaSize + (plane * chromaStride * (Height / 2));
            for (int y = 0; y < Height / 2; y++)
            {
                for (int x = 0; x < chromaStride; x++)
                {
                    pixels[offset + (y * chromaStride) + x] = (byte)(128 + (plane * 4));
                }
            }
        }
    }

    private static void FillSamples(byte[] samples, int count, int firstSample)
    {
        for (int index = 0; index < count; index++)
        {
            double phase = 2.0 * Math.PI * 440.0 * (firstSample + index) / SampleRate;
            short value = (short)(Math.Sin(phase) * short.MaxValue / 4);
            for (int channel = 0; channel < Channels; channel++)
            {
                int offset = ((index * Channels) + channel) * sizeof(short);
                samples[offset] = (byte)(value & 0xFF);
                samples[offset + 1] = (byte)((value >> 8) & 0xFF);
            }
        }
    }
}
