// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The same hand-written file as <see cref="RawAudioVideoFixtureFile"/>, with two streams of each kind so that a
/// switch can be measured: two pictures of different sizes and two sounds of different sample rates, interleaved.
/// <para>
/// Nothing is encoded and every frame is a keyframe, so a seek lands exactly where it asks and a decoder is the
/// only thing that has to understand the content.
/// </para>
/// </summary>
internal static partial class RawAudioVideoFixtureFile
{
    internal const int SecondWidth = 160;
    internal const int SecondHeight = 90;
    internal const int SecondSampleRate = 22050;

    /// <summary>
    /// Writes a file holding two video streams and two audio streams: <c>#0</c> is 320x180 and 44100 Hz,
    /// <c>#1</c> is 160x90 and 22050 Hz.
    /// </summary>
    internal static string CreateMultiTrack(int frameCount = 60)
    {
        FFmpegRuntime.EnsureInitialized();

        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-multitrack-fixture-{Guid.NewGuid():N}.mkv");
        int allocResult = avformat_alloc_output_context2(out nint muxer, 0, "matroska", path);
        Assert.That(allocResult, Is.GreaterThanOrEqualTo(0), $"muxer: {FFmpegInterop.GetErrorMessage(allocResult)}");

        nint io = 0;
        nint videoContext = 0;
        nint secondVideoContext = 0;
        nint audioContext = 0;
        nint secondAudioContext = 0;
        try
        {
            videoContext = OpenVideoCodec();
            secondVideoContext = OpenVideoCodec(SecondWidth, SecondHeight, "first");
            audioContext = OpenAudioCodec();
            secondAudioContext = OpenAudioCodec(SecondSampleRate, "jpn");

            nint videoStream = AddStream(muxer, videoContext);
            nint audioStream = AddStream(muxer, audioContext);
            nint secondVideoStream = AddStream(muxer, secondVideoContext);
            nint secondAudioStream = AddStream(muxer, secondAudioContext);

            int ioResult = avio_open(out io, path, AvioFlagWrite);
            Assert.That(ioResult, Is.GreaterThanOrEqualTo(0), $"io: {FFmpegInterop.GetErrorMessage(ioResult)}");
            Marshal.WriteIntPtr(muxer, (int)Marshal.OffsetOf<AVFormatContext>(nameof(AVFormatContext.IoContext)), io);

            int headerResult = avformat_write_header(muxer, 0);
            Assert.That(headerResult, Is.GreaterThanOrEqualTo(0), $"header: {FFmpegInterop.GetErrorMessage(headerResult)}");

            AVStream videoLayout = Marshal.PtrToStructure<AVStream>(videoStream);
            AVStream audioLayout = Marshal.PtrToStructure<AVStream>(audioStream);
            AVStream secondVideoLayout = Marshal.PtrToStructure<AVStream>(secondVideoStream);
            AVStream secondAudioLayout = Marshal.PtrToStructure<AVStream>(secondAudioStream);

            byte[] pixels = new byte[Width * Height * 3 / 2];
            byte[] secondPixels = new byte[SecondWidth * SecondHeight * 3 / 2];
            byte[] samples = new byte[SamplesPerAudioPacket * Channels * sizeof(short)];
            byte[] secondSamples = new byte[SamplesPerAudioPacket * Channels * sizeof(short)];

            int samplesWritten = 0;
            int secondSamplesWritten = 0;
            for (int index = 0; index < frameCount; index++)
            {
                FillPixels(pixels, index);
                WritePacket(muxer, videoLayout, pixels, pixels.Length, index, 1, FrameRate, PacketFlagKey);

                FillPixels(secondPixels, SecondWidth, SecondHeight, index);
                WritePacket(muxer, secondVideoLayout, secondPixels, secondPixels.Length, index, 1, FrameRate, PacketFlagKey);

                int wanted = SampleRate * (index + 1) / FrameRate;
                while (samplesWritten < wanted)
                {
                    int count = Math.Min(SamplesPerAudioPacket, wanted - samplesWritten);
                    FillSamples(samples, count, samplesWritten);
                    WritePacket(muxer, audioLayout, samples, count * Channels * sizeof(short), samplesWritten, count, SampleRate, 0);
                    samplesWritten += count;
                }

                int secondWanted = SecondSampleRate * (index + 1) / FrameRate;
                while (secondSamplesWritten < secondWanted)
                {
                    int count = Math.Min(SamplesPerAudioPacket, secondWanted - secondSamplesWritten);
                    FillSamples(secondSamples, SecondSampleRate, count, secondSamplesWritten);
                    WritePacket(muxer, secondAudioLayout, secondSamples, count * Channels * sizeof(short), secondSamplesWritten, count, SecondSampleRate, 0);
                    secondSamplesWritten += count;
                }
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

            if (secondVideoContext != 0)
            {
                FFmpegNative.avcodec_free_context(ref secondVideoContext);
            }

            if (audioContext != 0)
            {
                FFmpegNative.avcodec_free_context(ref audioContext);
            }

            if (secondAudioContext != 0)
            {
                FFmpegNative.avcodec_free_context(ref secondAudioContext);
            }

            if (io != 0)
            {
                avio_closep(ref io);
            }

            avformat_free_context(muxer);
        }

        return path;
    }

    /// <summary>A picture stream of its own size, so the two are told apart by the frames they produce.</summary>
    private static nint OpenVideoCodec(int width, int height, string title)
    {
        nint codec = avcodec_find_encoder_by_name("rawvideo");
        Assert.That(codec, Is.Not.EqualTo(0), "This FFmpeg build has no rawvideo codec.");

        var parameters = new AVCodecParameters
        {
            CodecType = MediaTypeVideo,
            CodecId = Marshal.ReadInt32(codec, (IntPtr.Size * 2) + sizeof(int)),
            Format = PixelFormatYuv420p,
            Width = width,
            Height = height,
            SampleAspectRatio = new AVRational { Numerator = 1, Denominator = 1 },
            FrameRate = new AVRational { Numerator = FrameRate, Denominator = 1 },
        };

        nint context = OpenCodec(codec, parameters, $"1/{FrameRate}");
        av_opt_set(context, "title", title, 0);
        return context;
    }

    /// <summary>A sound stream of its own rate, so the device's configuration says which one is playing.</summary>
    private static nint OpenAudioCodec(int sampleRate, string language)
    {
        nint codec = avcodec_find_encoder_by_name("pcm_s16le");
        Assert.That(codec, Is.Not.EqualTo(0), "This FFmpeg build has no pcm_s16le codec.");

        var parameters = new AVCodecParameters
        {
            CodecType = MediaTypeAudio,
            CodecId = Marshal.ReadInt32(codec, (IntPtr.Size * 2) + sizeof(int)),
            Format = SampleFormatS16,
            SampleRate = sampleRate,
            ChannelLayout = new AVChannelLayout { Order = ChannelOrderNative, ChannelCount = Channels, Layout = StereoChannelMask },
        };

        nint context = OpenCodec(codec, parameters, $"1/{sampleRate}");
        av_opt_set(context, "language", language, 0);
        return context;
    }

    private static void FillPixels(byte[] pixels, int width, int height, int index)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)((x + y + (index * 8)) & 0xFF);
            }
        }

        int lumaSize = width * height;
        int chromaStride = width / 2;
        for (int plane = 0; plane < 2; plane++)
        {
            int offset = lumaSize + (plane * chromaStride * (height / 2));
            for (int y = 0; y < height / 2; y++)
            {
                for (int x = 0; x < chromaStride; x++)
                {
                    pixels[offset + (y * chromaStride) + x] = (byte)(128 + (plane * 4));
                }
            }
        }
    }

    private static void FillSamples(byte[] samples, int sampleRate, int count, int firstSample)
    {
        for (int index = 0; index < count; index++)
        {
            double phase = 2.0 * Math.PI * 440.0 * (firstSample + index) / sampleRate;
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