// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// One packet may be handed to more than one chain, so its payload belongs to the last owner out rather than the
/// first — which is all that sharing a packet means.
/// </summary>
[TestFixture]
public class MediaPacketTests
{
    [Test]
    public void ReleasesThePayloadWhenTheLastOwnerLetsGo()
    {
        CountingBuffer buffer = new();
        MediaPacket packet = new()
        {
            StreamIndex = 0,
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(20),
            IsKeyFrame = true,
            Buffer = buffer,
        };

        packet.Ref();
        packet.Dispose();

        Assert.That(buffer.Disposals, Is.EqualTo(0), "the reader's share is gone but another chain is still reading it");

        packet.Dispose();

        Assert.That(buffer.Disposals, Is.EqualTo(1), "the payload goes with the last owner, once");
    }

    [Test]
    public void ReleasesThePayloadOfAPacketNobodyTook()
    {
        CountingBuffer buffer = new();
        MediaPacket packet = new()
        {
            StreamIndex = 0,
            Timestamp = TimeSpan.Zero,
            Duration = TimeSpan.FromMilliseconds(20),
            IsKeyFrame = true,
            Buffer = buffer,
        };

        packet.Dispose();

        Assert.That(buffer.Disposals, Is.EqualTo(1), "a packet no chain selected is released by the reader that read it");
    }

    private sealed class CountingBuffer : IMediaBuffer
    {
        private int _disposals;

        public long Length => 0;

        internal int Disposals => Volatile.Read(ref _disposals);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
        }
    }
}
