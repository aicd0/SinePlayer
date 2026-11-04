// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
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
    private string _subtitlePath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Fails loudly with a clear message when the native libraries are missing or mismatched.
        FFmpegRuntime.EnsureInitialized();
        _wavePath = CreateWaveFile();
        _subtitlePath = CreateSubtitleFile();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (File.Exists(_wavePath))
        {
            File.Delete(_wavePath);
        }

        if (File.Exists(_subtitlePath))
        {
            File.Delete(_subtitlePath);
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
            OperationResult result = source.TryReadPacket(out MediaPacket? packet);
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

        OperationResult result = source.Seek(TimeSpan.FromSeconds(DurationSeconds / 2), -1);
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

    /// <summary>
    /// A subtitle queue reads the whole file up front and refuses a backward seek that would have to answer with an
    /// event after the target — seeking to the start of a file whose first cue is a second in. The first cue is the
    /// right landing there, so the seek must reach it instead of failing and leaving the queue at its end.
    /// </summary>
    [TestCase(0.0)]
    [TestCase(0.5)]
    public void SeeksBeforeTheFirstCueToThatCue(double seconds)
    {
        FFmpegSourceProviderFactory factory = new();
        OperationResult result = factory.TryOpen(MediaLocator.Parse(_subtitlePath), out IMediaSource? source);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        using IMediaSource subtitleSource = source!;
        result = subtitleSource.Seek(TimeSpan.FromSeconds(seconds), -1);
        Assert.That(result.IsSuccessful, Is.True, result.Message);

        result = subtitleSource.TryReadPacket(out MediaPacket? packet);
        Assert.That(result.IsSuccessful, Is.True, result.Message);
        Assert.That(packet, Is.Not.Null, "The seek left the source at its end.");

        using (packet!)
        {
            Assert.That(packet!.Timestamp, Is.EqualTo(TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>
    /// The same event read twice has to read the same. libavcodec leads each event line with the number it counts its own
    /// output by, and a flush — which every seek does — sets that count back to zero, so the copy a jump re-reads used to
    /// differ from the copy still held: the window compared the two, found them different, and kept both.
    /// </summary>
    [Test]
    public void ReadsAnEventTheSameWayAgainAfterAJump()
    {
        FFmpegSourceProviderFactory factory = new();
        OperationResult opened = factory.TryOpen(MediaLocator.Parse(_subtitlePath), out IMediaSource? source);
        Assert.That(opened.IsSuccessful, Is.True, opened.Message);

        using IMediaSource subtitleSource = source!;
        SubtitleStreamInfo stream = subtitleSource.Info.Streams.OfType<SubtitleStreamInfo>().Single();
        OperationResult<FFmpegSubtitleDecoder> created = FFmpegSubtitleDecoder.Create(subtitleSource, stream);
        Assert.That(created.IsSuccessful, Is.True, created.Message);

        using FFmpegSubtitleDecoder decoder = created.Result;

        IReadOnlyList<(TimeSpan Start, string Text)> first = ReadEvents(subtitleSource, decoder);
        Assert.That(first, Has.Count.EqualTo(3));

        // The jump lands on the second event, so the decoder counts it as its first one: the copy that comes back carries
        // a different number from the copy already held unless the number is not part of the cue at all.
        Assert.That(decoder.Flush().IsSuccessful, Is.True);
        Assert.That(subtitleSource.Seek(first[1].Start, stream.Index).IsSuccessful, Is.True);

        IReadOnlyList<(TimeSpan Start, string Text)> again = ReadEvents(subtitleSource, decoder);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(again, Is.EqualTo(new[] { first[1], first[2] }), "the jump landed on the second event and read on from it");
            Assert.That(first[0].Text, Does.StartWith("0,Default,"), "a cue carries its layer and style, not the decoder's count of its own events");
        }
    }

    [Test]
    public void ReportsFailureForMissingFile()
    {
        FFmpegSourceProviderFactory factory = new();
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-missing-{Guid.NewGuid():N}.mkv");

        OperationResult result = factory.TryOpen(MediaLocator.Parse(path), out IMediaSource? source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(result.Message, Is.Not.Empty);
            Assert.That(source, Is.Null);
        }
    }

    /// <summary>
    /// A recording tagged with the orientation it was made at. The matrix sits in the stream's codec parameters, which
    /// is a part of the FFmpeg structures this project only declares as far as it reads, so this test is also what
    /// catches that declaration drifting away from the pinned build.
    /// </summary>
    [TestCase(90, false)]
    [TestCase(90, true)]
    [TestCase(180, true)]
    public void ReportsTheOrientationTheStreamIsTaggedWith(int rotation, bool mirrored)
    {
        string tagged = VideoFixtureFile.CreateH264(rotation: rotation, mirrored: mirrored);
        try
        {
            FFmpegSourceProviderFactory factory = new();
            OperationResult result = factory.TryOpen(MediaLocator.Parse(tagged), out IMediaSource? source);
            Assert.That(result.IsSuccessful, Is.True, result.Message);

            using (source)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(source!.Info.VideoStreams, Has.Count.EqualTo(1));

                    VideoStreamInfo video = source.Info.VideoStreams[0];
                    var expected = new VideoOrientation(-rotation / 90, mirrored);
                    Assert.That(video.Orientation, Is.EqualTo(expected), "the turn is read backwards, and a mirror is kept");
                    Assert.That(video.Width, Is.EqualTo(VideoFixtureFile.Width), "the stored size is not turned");
                    Assert.That(video.Height, Is.EqualTo(VideoFixtureFile.Height));
                }
            }
        }
        finally
        {
            File.Delete(tagged);
        }
    }

    [Test]
    public void ReportsNoOrientationForAStreamThatIsNotTagged()
    {
        string upright = VideoFixtureFile.CreateH264();
        try
        {
            FFmpegSourceProviderFactory factory = new();
            OperationResult result = factory.TryOpen(MediaLocator.Parse(upright), out IMediaSource? source);
            Assert.That(result.IsSuccessful, Is.True, result.Message);

            using (source)
            {
                Assert.That(source!.Info.VideoStreams[0].Orientation, Is.EqualTo(VideoOrientation.Identity));
            }
        }
        finally
        {
            File.Delete(upright);
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

    /// <summary>
    /// Everything the decoder produces from what is left of the source, as the cue's start and the event line it carries.
    /// </summary>
    private static IReadOnlyList<(TimeSpan Start, string Text)> ReadEvents(IMediaSource source, ISubtitleDecoder decoder)
    {
        List<(TimeSpan, string)> events = [];
        while (true)
        {
            OperationResult read = source.TryReadPacket(out MediaPacket? packet);
            Assert.That(read.IsSuccessful, Is.True, read.Message);
            if (packet is null)
            {
                return events;
            }

            using (packet)
            {
                OperationResult decoded = decoder.TryDecode(packet, out IReadOnlyList<SubtitleCue> cues);
                Assert.That(decoded.IsSuccessful, Is.True, decoded.Message);
                foreach (SubtitleCue cue in cues)
                {
                    using (cue)
                    {
                        events.Add((cue.Start, cue.Text ?? string.Empty));
                    }
                }
            }
        }
    }

    private static string CreateSubtitleFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sineplayer-ffmpeg-{Guid.NewGuid():N}.srt");
        File.WriteAllText(path, """
            1
            00:00:01,000 --> 00:00:02,000
            First cue

            2
            00:00:05,000 --> 00:00:06,000
            Second cue

            3
            00:00:09,000 --> 00:00:10,000
            Third cue

            """);

        return path;
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
