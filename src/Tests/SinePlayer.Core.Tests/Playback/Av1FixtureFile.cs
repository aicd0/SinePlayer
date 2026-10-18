// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Writes a real, temporally compressed video stream with libaom, muxed into an MP4. A file made this way
/// has a sequence header, reference frames and a keyframe structure, which is what the short fixtures
/// written by hand cannot have — and the keyframe structure is the whole point of <paramref name="gopSize"/>.
/// </summary>
internal static partial class Av1FixtureFile
{
    internal const int Width = 64;
    internal const int Height = 64;
    internal const int FrameCount = 80;

    private const int AvioFlagWrite = 2;
    private const int PixelFormatYuv420p = 0;

    [LibraryImport(FFmpegNative.AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint avcodec_find_encoder_by_name(string name);

    [LibraryImport(FFmpegNative.AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int av_opt_set(nint obj, string name, string value, int flags);

    [LibraryImport(FFmpegNative.AvUtil)]
    private static partial int av_frame_get_buffer(nint frame, int alignment);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int avcodec_send_frame(nint context, nint frame);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int avcodec_receive_packet(nint context, nint packet);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int avcodec_parameters_from_context(nint parameters, nint context);

    [LibraryImport(FFmpegNative.AvCodec)]
    private static partial int av_packet_rescale_ts(nint packet, Rational source, Rational destination);

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

    /// <param name="frameCount">How many frames the stream holds.</param>
    /// <param name="gopSize">
    /// What to ask the encoder for. libaom places its keyframes where it likes and takes no notice of this,
    /// so a caller that cares where a seek lands has to look at the stream rather than ask for it.
    /// </param>
    internal static string Create(int frameCount = FrameCount, int gopSize = 4)
    {
        FFmpegRuntime.EnsureInitialized();

        const int frameRate = 30;
        nint codec = avcodec_find_encoder_by_name("libaom-av1");
        if (codec == 0)
        {
            Assert.Ignore("This FFmpeg build has no libaom-av1 encoder to generate the fixture with.");
        }

        int mediaType = Marshal.ReadInt32(codec, IntPtr.Size * 2);
        int codecId = Marshal.ReadInt32(codec, (IntPtr.Size * 2) + 4);
        Assert.That(Marshal.PtrToStringAnsi(FFmpegNative.avcodec_get_name(codecId)), Is.EqualTo("av1"), "The AVCodec layout is not the one this fixture assumes.");

        nint context = FFmpegNative.avcodec_alloc_context3(codec);
        SetEncoderGeometry(context, mediaType, codecId, frameRate);
        Set(context, "time_base", $"1/{frameRate}");
        Set(context, "g", gopSize.ToString());
        Set(context, "b", "500000");
        Set(context, "threads", "1");

        int openResult = FFmpegNative.avcodec_open2(context, codec, 0);
        Assert.That(openResult, Is.GreaterThanOrEqualTo(0), $"open: {FFmpegInterop.GetErrorMessage(openResult)}");

        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-av1-fixture-{Guid.NewGuid():N}.mp4");
        int allocResult = avformat_alloc_output_context2(out nint muxer, 0, "mp4", path);
        Assert.That(allocResult, Is.GreaterThanOrEqualTo(0), $"muxer: {FFmpegInterop.GetErrorMessage(allocResult)}");

        nint stream = avformat_new_stream(muxer, 0);
        Assert.That(stream, Is.Not.EqualTo(0), "no stream");

        AVStream streamLayout = Marshal.PtrToStructure<AVStream>(stream);
        int parametersResult = avcodec_parameters_from_context(streamLayout.Codecpar, context);
        Assert.That(parametersResult, Is.GreaterThanOrEqualTo(0), $"parameters: {FFmpegInterop.GetErrorMessage(parametersResult)}");

        int ioResult = avio_open(out nint io, path, AvioFlagWrite);
        Assert.That(ioResult, Is.GreaterThanOrEqualTo(0), $"io: {FFmpegInterop.GetErrorMessage(ioResult)}");
        Marshal.WriteIntPtr(muxer, (int)Marshal.OffsetOf<AVFormatContext>(nameof(AVFormatContext.IoContext)), io);

        int headerResult = avformat_write_header(muxer, 0);
        Assert.That(headerResult, Is.GreaterThanOrEqualTo(0), $"header: {FFmpegInterop.GetErrorMessage(headerResult)}");

        var encoderTimeBase = new Rational { Numerator = 1, Denominator = frameRate };
        for (int index = 0; index < frameCount; index++)
        {
            nint frame = CreateFrame(index);
            int sendResult = avcodec_send_frame(context, frame);
            Assert.That(sendResult, Is.GreaterThanOrEqualTo(0), $"send: {FFmpegInterop.GetErrorMessage(sendResult)}");
            WritePackets(context, muxer, stream, encoderTimeBase);
            FFmpegNative.av_frame_unref(frame);
        }

        int flushResult = avcodec_send_frame(context, 0);
        Assert.That(flushResult, Is.GreaterThanOrEqualTo(0), $"flush: {FFmpegInterop.GetErrorMessage(flushResult)}");
        WritePackets(context, muxer, stream, encoderTimeBase);

        int trailerResult = av_write_trailer(muxer);
        Assert.That(trailerResult, Is.GreaterThanOrEqualTo(0), $"trailer: {FFmpegInterop.GetErrorMessage(trailerResult)}");

        FFmpegNative.avcodec_free_context(ref context);
        avio_closep(ref io);
        avformat_free_context(muxer);
        return path;
    }

    private static void SetEncoderGeometry(nint context, int mediaType, int codecId, int frameRate)
    {
        var parameters = new AVCodecParameters
        {
            CodecType = mediaType,
            CodecId = codecId,
            Format = PixelFormatYuv420p,
            Width = Width,
            Height = Height,
            SampleAspectRatio = new Rational { Numerator = 1, Denominator = 1 },
            Framerate = new Rational { Numerator = frameRate, Denominator = 1 },
            FieldOrder = 1,
            ColorRange = 1,
            ColorPrimaries = 1,
            ColorTrc = 1,
            ColorSpace = 1,
            ChromaLocation = 1,
        };

        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AVCodecParameters>());
        try
        {
            Marshal.StructureToPtr(parameters, buffer, false);
            int result = FFmpegNative.avcodec_parameters_to_context(context, buffer);
            Assert.That(result, Is.GreaterThanOrEqualTo(0), $"geometry: {FFmpegInterop.GetErrorMessage(result)}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint CreateFrame(int index)
    {
        nint frame = FFmpegNative.av_frame_alloc();
        Marshal.WriteInt32(frame + (int)Marshal.OffsetOf<AVFrame>(nameof(AVFrame.Width)), Width);
        Marshal.WriteInt32(frame + (int)Marshal.OffsetOf<AVFrame>(nameof(AVFrame.Height)), Height);
        Marshal.WriteInt32(frame + (int)Marshal.OffsetOf<AVFrame>(nameof(AVFrame.Format)), PixelFormatYuv420p);
        Marshal.WriteInt64(frame + (int)Marshal.OffsetOf<AVFrame>(nameof(AVFrame.Pts)), index);

        int bufferResult = av_frame_get_buffer(frame, 32);
        Assert.That(bufferResult, Is.GreaterThanOrEqualTo(0), $"buffer: {FFmpegInterop.GetErrorMessage(bufferResult)}");

        int strideOffset = (int)Marshal.OffsetOf<AVFrame>(nameof(AVFrame.LineSize));
        int lumaStride = Marshal.ReadInt32(frame + strideOffset);
        nint luma = Marshal.ReadIntPtr(frame);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Marshal.WriteByte(luma + (y * lumaStride) + x, (byte)((x + y + (index * 16)) & 0xFF));
            }
        }

        for (int plane = 1; plane <= 2; plane++)
        {
            nint chroma = Marshal.ReadIntPtr(frame, plane * IntPtr.Size);
            int chromaStride = Marshal.ReadInt32(frame + strideOffset + (plane * sizeof(int)));
            for (int y = 0; y < Height / 2; y++)
            {
                for (int x = 0; x < Width / 2; x++)
                {
                    Marshal.WriteByte(chroma + (y * chromaStride) + x, (byte)(128 + (plane * 8)));
                }
            }
        }

        return frame;
    }

    private static void WritePackets(nint context, nint muxer, nint stream, Rational encoderTimeBase)
    {
        while (true)
        {
            nint packet = FFmpegNative.av_packet_alloc();
            int result = avcodec_receive_packet(context, packet);
            if (result == FFmpegConstants.ErrorAgain || result == FFmpegConstants.ErrorEndOfStream)
            {
                FFmpegNative.av_packet_free(ref packet);
                return;
            }

            if (result < 0)
            {
                FFmpegNative.av_packet_free(ref packet);
                Assert.Fail($"receive: {FFmpegInterop.GetErrorMessage(result)}");
            }

            Marshal.WriteInt32(packet + (int)Marshal.OffsetOf<AVPacket>(nameof(AVPacket.StreamIndex)), 0);
            AVStream streamLayout = Marshal.PtrToStructure<AVStream>(stream);
            av_packet_rescale_ts(packet, encoderTimeBase, streamLayout.TimeBase);

            int writeResult = av_write_frame(muxer, packet);
            FFmpegNative.av_packet_free(ref packet);
            Assert.That(writeResult, Is.GreaterThanOrEqualTo(0), $"write: {FFmpegInterop.GetErrorMessage(writeResult)}");
        }
    }

    private static void Set(nint context, string name, string value)
    {
        // Optional encoder settings: a build that does not know one of them still encodes.
        av_opt_set(context, name, value, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public int Numerator;
        public int Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVCodecParameters
    {
        public int CodecType;
        public int CodecId;
        public uint CodecTag;
        public nint Extradata;
        public int ExtradataSize;
        public nint CodedSideData;
        public int CodedSideDataCount;
        public int Format;
        public long BitRate;
        public int BitsPerCodedSample;
        public int BitsPerRawSample;
        public int Profile;
        public int Level;
        public int Width;
        public int Height;
        public Rational SampleAspectRatio;
        public Rational Framerate;
        public int FieldOrder;
        public int ColorRange;
        public int ColorPrimaries;
        public int ColorTrc;
        public int ColorSpace;
        public int ChromaLocation;
        public int VideoDelay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AVStream
    {
        public nint AvClass;
        public int Index;
        public int Id;
        public nint Codecpar;
        public nint PrivateData;
        public Rational TimeBase;
    }
}
