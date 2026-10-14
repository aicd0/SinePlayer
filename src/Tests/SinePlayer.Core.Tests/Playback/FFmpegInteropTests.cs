// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

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
}
