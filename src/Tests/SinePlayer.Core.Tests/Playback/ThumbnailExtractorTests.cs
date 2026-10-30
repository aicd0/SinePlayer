// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Thumbnails;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Draws posters out of real files. The pictures a shell thumbnail can give are out of reach here — that is a Windows
/// service — so what is checked is the part this project owns: that a frame comes out of a file, at the size asked
/// for, in the shape the video has.
/// </summary>
[TestFixture]
public class ThumbnailExtractorTests
{
    private const int MaxEdge = 320;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Fails loudly with a clear message when the native libraries are missing or mismatched.
        FFmpegRuntime.EnsureInitialized();
    }

    [Test]
    public void DrawsAPictureInTheShapeOfTheVideo()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            OperationResult<MediaThumbnail> extracted = Extract(path, MaxEdge);

            Assert.That(extracted.IsSuccessful, Is.True, extracted.ErrorMessage);
            MediaThumbnail thumbnail = extracted.Result;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Math.Max(thumbnail.Width, thumbnail.Height), Is.EqualTo(MaxEdge), "the longest edge is the size asked for");
                Assert.That(thumbnail.Width, Is.GreaterThan(thumbnail.Height), "the fixture is a landscape picture");
                Assert.That((double)thumbnail.Width / thumbnail.Height, Is.EqualTo(320.0 / 180.0).Within(0.02), "the shape is the video's, not a fixed one");
                Assert.That(thumbnail.Pixels, Has.Length.EqualTo(thumbnail.Width * thumbnail.Height * 4));
                Assert.That(thumbnail.Stride, Is.EqualTo(thumbnail.Width * 4));
                Assert.That(thumbnail.Pixels, Has.Some.Not.EqualTo(0), "the picture has pixels in it");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NeverEnlargesAPictureSmallerThanTheSizeAskedFor()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            OperationResult<MediaThumbnail> extracted = Extract(path, maxEdge: 1920);

            Assert.That(extracted.IsSuccessful, Is.True, extracted.ErrorMessage);
            Assert.That(extracted.Result.Width, Is.EqualTo(320), "a small video is not blown up to the size asked for");
            Assert.That(extracted.Result.Height, Is.EqualTo(180));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReportsAFileThatHoldsNoPicture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sine-thumbnail-{Guid.NewGuid():N}.wav");
        WriteSilentWaveFile(path);
        try
        {
            OperationResult<MediaThumbnail> extracted = Extract(path, MaxEdge);

            Assert.That(extracted.IsSuccessful, Is.False);
            Assert.That(extracted.ErrorMessage, Does.Contain("no video stream"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ReportsAFileThatDoesNotOpen()
    {
        OperationResult<MediaThumbnail> extracted = Extract(@"Z:\nowhere\missing-file.mkv", MaxEdge);

        Assert.That(extracted.IsSuccessful, Is.False, "a file that cannot be opened is a failure, not an exception");
    }

    /// <summary>
    /// A position a file does not reach — a clock that was left at an old position, a duration that outlasts the
    /// frames. Nothing comes out at or after it, and a poster that is a little early beats no poster at all.
    /// </summary>
    [Test]
    public void DrawsAClipThatEndsBeforeThePositionAskedFor()
    {
        string path = VideoFixtureFile.CreateH264(frameCount: 10, gopSize: 10);
        try
        {
            OperationResult<MediaThumbnail> extracted = MediaThumbnailExtractor.TryExtract(
                new FFmpegSourceProviderFactory(),
                new FFmpegVideoDecoderFactory(),
                MediaLocator.Parse(path),
                TimeSpan.FromSeconds(30),
                MaxEdge);

            Assert.That(extracted.IsSuccessful, Is.True, extracted.ErrorMessage);
            Assert.That(extracted.Result.Width, Is.EqualTo(VideoFixtureFile.Width));
            Assert.That(extracted.Result.Height, Is.EqualTo(VideoFixtureFile.Height));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OperationResult<MediaThumbnail> Extract(string path, int maxEdge)
    {
        return MediaThumbnailExtractor.TryExtract(
            new FFmpegSourceProviderFactory(),
            new FFmpegVideoDecoderFactory(),
            MediaLocator.Parse(path),
            position: null,
            maxEdge);
    }

    private static void WriteSilentWaveFile(string path)
    {
        const int sampleRate = 8000;
        const int sampleCount = 4000;
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        int dataSize = sampleCount * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (int i = 0; i < sampleCount; i++)
        {
            writer.Write((short)0);
        }
    }
}
