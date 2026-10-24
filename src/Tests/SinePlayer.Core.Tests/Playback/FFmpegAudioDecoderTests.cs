// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Exercises the software audio decoder against a real bitstream. The fixture is a wave file written by
/// the test, which FFmpeg demuxes as a single audio stream and decodes through the same codec plumbing
/// any audio stream uses.
/// </summary>
[TestFixture]
public class FFmpegAudioDecoderTests
{
    private const int SampleRate = 44100;
    private const int ChannelCount = 2;
    private const int SampleCount = 4410;

    private string _wavePath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        FFmpegRuntime.EnsureInitialized();
        _wavePath = CreateWaveFile();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (File.Exists(_wavePath))
        {
            File.Delete(_wavePath);
        }
    }

    [Test]
    public void DecodesTheSamplesExactly()
    {
        using FFmpegSource source = OpenWaveFile();
        using IAudioDecoder decoder = CreateDecoder(source);

        List<RawAudioBuffer> buffers = DecodeAll(source, decoder);

        Assert.That(buffers, Is.Not.Empty);

        int sampleCount = 0;
        using (MemoryStream samples = new())
        {
            foreach (RawAudioBuffer buffer in buffers)
            {
                using (buffer)
                {
                    sampleCount += buffer.SampleCount;
                    var planar = (IPlanarAudioBuffer)buffer.Buffer;
                    for (int plane = 0; plane < planar.PlaneCount; plane++)
                    {
                        byte[] planeBytes = new byte[planar.GetPlaneByteCount(plane)];
                        Marshal.Copy(planar.GetPlanePointer(plane), planeBytes, 0, planeBytes.Length);
                        samples.Write(planeBytes, 0, planeBytes.Length);
                    }
                }
            }

            // A wave file carries PCM, so decoding it must reproduce the samples byte for byte: no
            // resampling, no format conversion, no loss.
            Assert.That(samples.ToArray(), Is.EqualTo(CreateExpectedSamples()));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sampleCount, Is.EqualTo(SampleCount));
            Assert.That(decoder.IsHardwareAccelerated, Is.False);
            Assert.That(decoder.OutputFormat, Is.EqualTo(AudioSampleFormat.Int16));
        }
    }

    [Test]
    public void ReportsTheStreamsAudioShape()
    {
        using FFmpegSource source = OpenWaveFile();
        using IAudioDecoder decoder = CreateDecoder(source);

        Assert.That(decoder.Stream, Is.InstanceOf<AudioStreamInfo>());
        var stream = (AudioStreamInfo)decoder.Stream;

        List<RawAudioBuffer> buffers = DecodeAll(source, decoder);
        Assert.That(buffers, Is.Not.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.SampleRate, Is.EqualTo(SampleRate));
            Assert.That(stream.ChannelCount, Is.EqualTo(ChannelCount));
            Assert.That(stream.ChannelLayout, Is.EqualTo(ChannelLayout.Stereo));

            RawAudioBuffer first = buffers[0];
            Assert.That(first.SampleRate, Is.EqualTo(SampleRate));
            Assert.That(first.ChannelCount, Is.EqualTo(ChannelCount));
            Assert.That(first.ChannelLayout, Is.EqualTo(ChannelLayout.Stereo));
            Assert.That(first.SampleFormat, Is.EqualTo(AudioSampleFormat.Int16));
            Assert.That(first.Timestamp, Is.EqualTo(TimeSpan.Zero));

            // Interleaved samples live in a single plane, and a buffer knows how long it is.
            Assert.That(first.Buffer, Is.InstanceOf<IPlanarAudioBuffer>());
            var planar = (IPlanarAudioBuffer)first.Buffer;
            Assert.That(planar.PlaneCount, Is.EqualTo(1));
            Assert.That(planar.GetPlaneByteCount(0), Is.EqualTo(first.SampleCount * ChannelCount * sizeof(short)));
            Assert.That(planar.Length, Is.EqualTo(planar.GetPlaneByteCount(0)));
        }

        DisposeBuffers(buffers);
    }

    [Test]
    public void RejectsPacketsFromAnotherBackend()
    {
        using FFmpegSource source = OpenWaveFile();
        using IAudioDecoder decoder = CreateDecoder(source);

        using MediaPacket packet = new()
        {
            StreamIndex = 0,
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            IsKeyFrame = true,
            Buffer = new ForeignBuffer(),
        };

        OperationResult result = decoder.TryDecode(packet, out IReadOnlyList<RawAudioBuffer> buffers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(buffers, Is.Empty);
        }
    }

    private static void DisposeBuffers(List<RawAudioBuffer> buffers)
    {
        foreach (RawAudioBuffer buffer in buffers)
        {
            buffer.Dispose();
        }
    }

    private FFmpegSource OpenWaveFile()
    {
        FFmpegSourceProviderFactory factory = new();
        OperationResult result = factory.TryOpen(MediaLocator.Parse(_wavePath), out IMediaSource? source);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(source, Is.InstanceOf<FFmpegSource>());

        return (FFmpegSource)source!;
    }

    private static IAudioDecoder CreateDecoder(FFmpegSource source)
    {
        Assert.That(source.Info.AudioStreams, Is.Not.Empty);
        AudioStreamInfo stream = source.Info.AudioStreams[0];

        FFmpegAudioDecoderFactory factory = new();
        OperationResult result = factory.TryCreate(source, stream, DecoderOptions.Default, out IComponentCreator<IAudioDecoder>? creator);

        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(creator, Is.Not.Null);

        OperationResult created = creator!.Create(out IAudioDecoder? decoder);
        Assert.That(created.IsSuccessful, Is.True, created.Message);
        Assert.That(decoder, Is.Not.Null);

        return decoder!;
    }

    private static List<RawAudioBuffer> DecodeAll(FFmpegSource source, IAudioDecoder decoder)
    {
        List<RawAudioBuffer> buffers = [];
        while (true)
        {
            OperationResult readResult = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(readResult.IsSuccessful, Is.True, readResult.Message);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                if (packet.StreamIndex != decoder.Stream.Index)
                {
                    continue;
                }

                OperationResult decodeResult = decoder.TryDecode(packet, out IReadOnlyList<RawAudioBuffer> decoded);
                Assert.That(decodeResult.IsSuccessful, Is.True, decodeResult.Message);
                buffers.AddRange(decoded);
            }
        }

        OperationResult drainResult = decoder.Drain(out IReadOnlyList<RawAudioBuffer> remaining);
        Assert.That(drainResult.IsSuccessful, Is.True, drainResult.Message);
        buffers.AddRange(remaining);

        return buffers;
    }

    private sealed class ForeignBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }

    private static byte[] CreateExpectedSamples()
    {
        byte[] samples = new byte[SampleCount * ChannelCount * sizeof(short)];
        int offset = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            short left = (short)(((i % 100) * 300) - 15000);
            short right = (short)((((i + 50) % 100) * 300) - 15000);
            samples[offset++] = (byte)(left & 0xFF);
            samples[offset++] = (byte)((left >> 8) & 0xFF);
            samples[offset++] = (byte)(right & 0xFF);
            samples[offset++] = (byte)((right >> 8) & 0xFF);
        }

        return samples;
    }

    /// <summary>Writes an uncompressed wave file, which needs no encoder to produce.</summary>
    private static string CreateWaveFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-audio-{Guid.NewGuid():N}.wav");
        byte[] samples = CreateExpectedSamples();

        using (BinaryWriter writer = new(File.Create(path)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)ChannelCount);
            writer.Write(SampleRate);
            writer.Write(SampleRate * ChannelCount * sizeof(short));
            writer.Write((short)(ChannelCount * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples.Length);
            writer.Write(samples);
        }

        return path;
    }
}
