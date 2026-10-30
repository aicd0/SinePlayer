// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Timestamp conversion is the one piece of arithmetic every stage depends on, and it is worth pinning
/// on its own: a bug here is invisible in short fixtures and catastrophic in real files, because the
/// synchronizer compares these values against the clock.
/// </summary>
[TestFixture]
public class FFmpegInteropTests
{
    [Test]
    public void ConvertsTimestampsThroughoutAFile()
    {
        var perNinetyThousand = new AVRational { Numerator = 1, Denominator = 90000 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegInterop.ToTimeSpan(0, perNinetyThousand), Is.EqualTo(TimeSpan.Zero));
            Assert.That(FFmpegInterop.ToTimeSpan(45000, perNinetyThousand), Is.EqualTo(TimeSpan.FromSeconds(0.5)));
            Assert.That(FFmpegInterop.ToTimeSpan(90000, perNinetyThousand), Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(FFmpegInterop.ToTimeSpan(180000, perNinetyThousand), Is.EqualTo(TimeSpan.FromSeconds(2)));

            // The regression: anything past a second used to come back as zero, which made every frame
            // after the first second of a file look infinitely late.
            Assert.That(FFmpegInterop.ToTimeSpan(1800000, perNinetyThousand), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(FFmpegInterop.ToTimeSpan(108000000, perNinetyThousand), Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(FFmpegInterop.ToTimeSpan(3600 * 1000, new AVRational { Numerator = 1, Denominator = 1000 }), Is.EqualTo(TimeSpan.FromHours(1)));
        }
    }

    [Test]
    public void ReadsMissingAndNegativeTimestampsAsTheStart()
    {
        var perThirtieth = new AVRational { Numerator = 1, Denominator = 30 };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegInterop.ToTimeSpan(FFmpegConstants.NoPtsValue, perThirtieth), Is.EqualTo(TimeSpan.Zero));
            Assert.That(FFmpegInterop.ToTimeSpan(-30, perThirtieth), Is.EqualTo(TimeSpan.Zero));
            Assert.That(FFmpegInterop.ToTimeSpan(30, new AVRational { Numerator = 1, Denominator = 0 }), Is.EqualTo(TimeSpan.Zero));
        }
    }

    /// <summary>
    /// A track's own length as Matroska writes it in its <c>DURATION</c> tag, which is the only place that container
    /// says how long a stream is: two streams of one file end at different moments, and a seek past one of those ends
    /// is read against this number.
    /// </summary>
    [Test]
    public void ReadsATracksLengthFromItsDurationTag()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegInterop.ParseDurationTag("00:03:13.000000000"), Is.EqualTo(TimeSpan.FromSeconds(193)));
            Assert.That(FFmpegInterop.ParseDurationTag("00:03:30.767000000"), Is.EqualTo(TimeSpan.FromMilliseconds(210767)));
            Assert.That(FFmpegInterop.ParseDurationTag("01:02:03.5"), Is.EqualTo(new TimeSpan(1, 2, 3) + TimeSpan.FromMilliseconds(500)));

            // Nine digits where a TimeSpan counts seven: the rest is below what anything can be scheduled at.
            Assert.That(FFmpegInterop.ParseDurationTag("00:00:01.123456789"), Is.EqualTo(TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1234567)));

            // Anything else is no answer at all — a length that cannot be read must not be guessed at.
            Assert.That(FFmpegInterop.ParseDurationTag(null), Is.Null);
            Assert.That(FFmpegInterop.ParseDurationTag(""), Is.Null);
            Assert.That(FFmpegInterop.ParseDurationTag("3:13"), Is.Null);
            Assert.That(FFmpegInterop.ParseDurationTag("soon"), Is.Null);
            Assert.That(FFmpegInterop.ParseDurationTag("00:00:0x.5"), Is.Null);
        }
    }

    /// <summary>
    /// How to read a picture's samples, taken from the values FFmpeg reports. The numbers are FFmpeg's own, so the ones
    /// that matter are pinned: SMPTE 2084 is what makes an HDR10 frame a set of light levels, and reading its samples as
    /// anything else is what leaves the picture dim. An enumeration this build does not name stays unknown rather than
    /// being guessed at, because a wrong transfer turns a picture grey.
    /// </summary>
    [Test]
    public void MapsColourMetadataFromFFmpegEnumerations()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FFmpegInterop.ToColourInfo(9, 16, 1), Is.EqualTo(new ColourInfo(ColourPrimaries.Bt2020, ColourTransfer.Smpte2084, ColourRange.Limited)));
            Assert.That(FFmpegInterop.ToColourInfo(9, 18, 1), Is.EqualTo(new ColourInfo(ColourPrimaries.Bt2020, ColourTransfer.Hlg, ColourRange.Limited)));
            Assert.That(FFmpegInterop.ToColourInfo(1, 1, 1), Is.EqualTo(new ColourInfo(ColourPrimaries.Bt709, ColourTransfer.Bt709, ColourRange.Limited)));
            Assert.That(FFmpegInterop.ToColourInfo(1, 1, 2), Is.EqualTo(new ColourInfo(ColourPrimaries.Bt709, ColourTransfer.Bt709, ColourRange.Full)));

            // Untagged is the state of most files, and it has to stay unknown: 2 is "unspecified" to FFmpeg.
            Assert.That(FFmpegInterop.ToColourInfo(2, 2, 0), Is.EqualTo(ColourInfo.Unknown));

            // A transfer this build has no programme for is not turned into one it does.
            Assert.That(FFmpegInterop.ToColourInfo(9, 14, 1), Is.EqualTo(new ColourInfo(ColourPrimaries.Bt2020, ColourTransfer.Unknown, ColourRange.Limited)));
        }
    }

    /// <summary>
    /// The display matrix a phone writes when it records sideways. What a player applies is the inverse of that matrix,
    /// so the sign is the whole point: the matrix <c>-display_rotation 90</c> produces has to come back as three
    /// quarter turns clockwise, or every portrait recording is drawn upside down. A negative determinant is a mirrored
    /// picture, and a mirror reverses the sense of the turn.
    /// </summary>
    [Test]
    public unsafe void ReadsTheOrientationADisplayMatrixAsksFor()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Orientation(65536, 0, 0, 65536), Is.EqualTo(VideoOrientation.Identity), "no turn");
            Assert.That(Orientation(0, -65536, 65536, 0), Is.EqualTo(new VideoOrientation(3, false)), "what -display_rotation 90 writes");
            Assert.That(Orientation(-65536, 0, 0, -65536), Is.EqualTo(new VideoOrientation(2, false)), "half a turn");
            Assert.That(Orientation(0, 65536, -65536, 0), Is.EqualTo(new VideoOrientation(1, false)), "90 degrees clockwise");

            Assert.That(Orientation(-65536, 0, 0, 65536), Is.EqualTo(new VideoOrientation(0, true)), "a horizontal flip");
            Assert.That(Orientation(65536, 0, 0, -65536), Is.EqualTo(new VideoOrientation(2, true)), "a vertical flip");
            Assert.That(Orientation(0, 65536, 65536, 0), Is.EqualTo(new VideoOrientation(1, true)), "a quarter turn and a flip");

            Assert.That(Orientation(0, 0, 0, 0), Is.EqualTo(VideoOrientation.Identity), "an empty matrix is not a transform");
            Assert.That(Orientation(65536, 1000, -1000, 65536), Is.EqualTo(VideoOrientation.Identity), "a correction of a degree is not a turn");

            // What a matrix can also carry is dropped on purpose: a scale or an offset is not a transform a surface is
            // shown at, and FFmpeg's own accessor ignores them as well.
            Assert.That(Orientation(32768, 0, 0, 32768), Is.EqualTo(VideoOrientation.Identity), "a scale is not an orientation");
            Assert.That(Orientation(0, -65536, 65536, 0, 4096, 8192), Is.EqualTo(new VideoOrientation(3, false)), "where the picture is moved to is not part of its orientation");
        }

        // The linear part as the matrix is written: x' = a x + b y, y' = c x + d y; u and v are where it is moved to.
        static VideoOrientation Orientation(int a, int b, int c, int d, int u = 0, int v = 0)
        {
            int* matrix = stackalloc int[9];
            matrix[0] = a;
            matrix[1] = b;
            matrix[2] = u;
            matrix[3] = c;
            matrix[4] = d;
            matrix[5] = v;
            matrix[6] = 0;
            matrix[7] = 0;
            matrix[8] = 1 << 30;
            return FFmpegInterop.ToOrientation(matrix);
        }
    }
}
