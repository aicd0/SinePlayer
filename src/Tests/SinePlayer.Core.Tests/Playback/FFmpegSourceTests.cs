// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Exercises the FFmpeg source against a real container. The fixture is generated rather than checked
/// in, so the test has no binary dependency.
/// </summary>
[TestFixture]
public class FFmpegSourceTests
{
    private const int SampleRate = 8000;
    private const int ChannelCount = 1;
    private const int SampleCount = 4000;
    private const double DurationSeconds = (double)SampleCount / SampleRate;

    private string _wavePath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Fails loudly with a clear message when the native libraries are missing or mismatched.
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
    public void ReportsContainerAndStreamFacts()
    {
        using FFmpegSource source = OpenWaveFile();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Info.ContainerId, Is.EqualTo("wav"));
            Assert.That(source.Info.Duration.TotalSeconds, Is.EqualTo(DurationSeconds).Within(0.05));
            Assert.That(source.Info.AudioStreams, Has.Count.EqualTo(1));

            AudioStreamInfo audio = source.Info.AudioStreams[0];
            Assert.That(audio.CodecId, Is.EqualTo("pcm_s16le"));
            Assert.That(audio.SampleRate, Is.EqualTo(SampleRate));
            Assert.That(audio.ChannelCount, Is.EqualTo(ChannelCount));
            Assert.That(audio.ChannelLayout, Is.EqualTo(ChannelLayout.Mono));
            Assert.That(audio.SampleFormat, Is.EqualTo(AudioSampleFormat.Int16));
            Assert.That(audio.Kind, Is.EqualTo(MediaStreamKind.Audio));
        }
    }

    [Test]
    public void ReadsPacketsUntilEndOfStream()
    {
        using FFmpegSource source = OpenWaveFile();

        int packetCount = 0;
        while (true)
        {
            PlaybackResult result = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                Assert.That(packet.StreamIndex, Is.EqualTo(0));
                Assert.That(packet.Buffer.Length, Is.GreaterThan(0));
                Assert.That(packet.Timestamp, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            }

            packetCount++;
            Assert.That(packetCount, Is.LessThan(1000), "Reading did not reach the end of the stream.");
        }

        Assert.That(packetCount, Is.GreaterThan(0));
    }

    [Test]
    public void SeekRestartsReadingNearTheRequestedPosition()
    {
        using FFmpegSource source = OpenWaveFile();

        PlaybackResult result = source.Seek(TimeSpan.FromSeconds(DurationSeconds / 2));
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        result = source.TryReadPacket(out MediaPacket? packet);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(packet, Is.Not.Null);

        using (packet!)
        {
            Assert.That(packet!.Timestamp.TotalSeconds, Is.GreaterThanOrEqualTo(0.2));
            Assert.That(packet!.Timestamp.TotalSeconds, Is.LessThan(DurationSeconds));
        }
    }

    [Test]
    public void ReportsFailureForMissingFile()
    {
        FFmpegSourceProviderFactory factory = new();
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-missing-{Guid.NewGuid():N}.mkv");

        PlaybackResult result = factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(result.Message, Is.Not.Empty);
            Assert.That(source, Is.Null);
        }
    }

    [Test]
    public void ProviderDeclaresNoSchemeRestriction()
    {
        FFmpegSourceProviderFactory factory = new();

        Assert.That(factory.Capabilities.Schemes, Is.Null);
    }

    private FFmpegSource OpenWaveFile()
    {
        FFmpegSourceProviderFactory factory = new();
        PlaybackResult result = factory.TryOpen(MediaLocator.Parse(_wavePath), out IMediaSource? source);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(source, Is.InstanceOf<FFmpegSource>());

        return (FFmpegSource)source!;
    }

    private static string CreateWaveFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-ffmpeg-{Guid.NewGuid():N}.wav");
        using (BinaryWriter writer = new(File.Create(path)))
        {
            int dataSize = SampleCount * ChannelCount * sizeof(short);

            writer.Write("RIFF"u8);
            writer.Write(36 + dataSize);
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
            writer.Write(dataSize);

            for (int i = 0; i < SampleCount; i++)
            {
                writer.Write((short)(Math.Sin(i * 0.25) * 8000));
            }
        }

        return path;
    }
}
