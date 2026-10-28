// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// How long a decoded event stays on screen. Text formats state both ends in the event, so the packet is only ever a
/// fallback; a bitmap format states the start and reports no end at all, which is the case that has to become a cue the
/// window can close later rather than a cue with a made-up length.
/// </summary>
[TestFixture]
public class SubtitleCueEndTests
{
    [Test]
    public void TakesTheEndTheTrackStates()
    {
        AVSubtitle eventData = new() { StartDisplayTime = 0, EndDisplayTime = 2500 };

        TimeSpan end = FFmpegSubtitleDecoder.ReadEnd(eventData, Packet(TimeSpan.FromSeconds(10), TimeSpan.Zero), TimeSpan.FromSeconds(10));

        Assert.That(end, Is.EqualTo(TimeSpan.FromSeconds(12.5)));
    }

    [Test]
    public void LeavesACueOpenWhenTheTrackSaysTheEndIsUnstated()
    {
        // What libavcodec reports for a PGS display set: the start and nothing else, with UINT32_MAX standing for "the
        // next event ends this one". Read as a duration it is 49 days, which is how cues came to pile up.
        AVSubtitle eventData = new() { StartDisplayTime = 0, EndDisplayTime = uint.MaxValue };

        TimeSpan end = FFmpegSubtitleDecoder.ReadEnd(eventData, Packet(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(10));

        Assert.That(end, Is.EqualTo(SubtitleCue.OpenEnd), "an unstated end is not the packet's own duration either");
    }

    [Test]
    public void FallsBackToThePacketsDurationWhenTheEventStatesNothing()
    {
        AVSubtitle eventData = new() { StartDisplayTime = 0, EndDisplayTime = 0 };

        TimeSpan end = FFmpegSubtitleDecoder.ReadEnd(eventData, Packet(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(10));

        Assert.That(end, Is.EqualTo(TimeSpan.FromSeconds(12)), "a container that leaves the length to the packet is believed");
    }

    [Test]
    public void LeavesACueOpenWhenThereIsNothingToFallBackOn()
    {
        AVSubtitle eventData = new() { StartDisplayTime = 0, EndDisplayTime = 0 };

        TimeSpan end = FFmpegSubtitleDecoder.ReadEnd(eventData, Packet(TimeSpan.FromSeconds(10), TimeSpan.Zero), TimeSpan.FromSeconds(10));

        Assert.That(end, Is.EqualTo(SubtitleCue.OpenEnd), "a cue with no stated length and no packet length would otherwise never be seen");
    }

    [Test]
    public void IgnoresAnEndThatIsNotAfterTheStart()
    {
        AVSubtitle eventData = new() { StartDisplayTime = 500, EndDisplayTime = 200 };

        TimeSpan end = FFmpegSubtitleDecoder.ReadEnd(eventData, Packet(TimeSpan.FromSeconds(10), TimeSpan.Zero), TimeSpan.FromSeconds(10.5));

        Assert.That(end, Is.EqualTo(SubtitleCue.OpenEnd), "a span that runs backwards is no span at all");
    }

    private static MediaPacket Packet(TimeSpan timestamp, TimeSpan duration)
    {
        return new MediaPacket
        {
            StreamIndex = 0,
            Timestamp = timestamp,
            Duration = duration,
            IsKeyFrame = true,
            Buffer = new EmptyBuffer(),
        };
    }

    private sealed class EmptyBuffer : IMediaBuffer
    {
        public long Length => 0;

        public void Dispose()
        {
        }
    }
}
