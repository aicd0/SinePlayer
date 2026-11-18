// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Clamp;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The last stage of the audio chain is where headroom is given up: everything before it hands peaks on as they are,
/// so this is the one place a sample above full scale becomes full scale. It writes where the block already is, because
/// nothing after it reads that block again, and a value that is not a number is a fault it silences.
/// </summary>
[TestFixture]
public class ClampAudioFilterTests
{
    private const int SampleRate = 48000;

    /// <summary>Full scale is the limit in both directions, and what is already inside it is left exactly as it was.</summary>
    [Test]
    public void ClampsSamplesToFullScale()
    {
        using ClampAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(2.0f, -3.0f, 0.5f, -0.25f, 0.0f);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Is.SameAs(block), "the block is clamped where it lies, not copied");
            Assert.That(ReadSamples(output), Is.EqualTo(new[] { 1.0, -1.0, 0.5, -0.25, 0.0 }));
        }
    }

    /// <summary>
    /// A value that is not a number has no level to be clamped to, and no stage is supposed to produce one: it is
    /// silenced so the block can still play, and the fault is reported rather than averaged into the sound. It is not
    /// counted as clipping — nothing about it was too loud.
    /// </summary>
    [Test]
    public void SilencesSamplesThatAreNotNumbers()
    {
        using ClampAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadSamples(output), Is.EqualTo(new[] { 0.0, 0.0, 0.0, 0.5 }));
            Assert.That(filter.ClippedSamples, Is.Zero, "a fault is not a level that was too loud");
        }
    }

    /// <summary>
    /// What the stage clips is counted as it goes, which is the figure a listener can read as "the mix is louder than
    /// the samples can hold": the samples that went past full scale, and only those, over the life of the stage.
    /// </summary>
    [Test]
    public void CountsTheSamplesItClips()
    {
        using ClampAudioFilter filter = new();
        using RawAudioBuffer first = CreateBlock(2.0f, -3.0f, 0.5f, -0.25f);

        Assert.That(filter.Apply(first, out _).IsSuccessful, Is.True);
        Assert.That(filter.ClippedSamples, Is.EqualTo(2), "only the two that went past full scale");

        using RawAudioBuffer second = CreateBlock(-1.5f, 0.0f, 0.0f, 0.0f);
        Assert.That(filter.Apply(second, out _).IsSuccessful, Is.True);
        Assert.That(filter.ClippedSamples, Is.EqualTo(3), "and the figure keeps counting across blocks");
    }

    /// <summary>
    /// Every plane of a planar block is clamped, not only the first: a mix that overshoots on the right channel is as
    /// much this stage's business as one that overshoots on the left.
    /// </summary>
    [Test]
    public void ClampsEveryPlane()
    {
        using ClampAudioFilter filter = new();
        using RawAudioBuffer block = CreatePlanarBlock(AudioSampleFormat.Float32, ChannelLayout.Stereo, 2, 2, [ToBytes([1.5f, -1.5f]), ToBytes([0.25f, 4.0f])]);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadPlanes(output)[0], Is.EqualTo(new[] { 1.0, -1.0 }));
            Assert.That(ReadPlanes(output)[1], Is.EqualTo(new[] { 0.25, 1.0 }));
        }
    }

    /// <summary>
    /// A block in any other format is a chain that is not the one it was prepared for: it fails rather than reading
    /// whole samples as if they were levels.
    /// </summary>
    [Test]
    public void RefusesABlockInAnyOtherFormat()
    {
        using ClampAudioFilter filter = new();
        using RawAudioBuffer block = CreatePlanarBlock(AudioSampleFormat.Int16, ChannelLayout.Mono, 1, 4, [new byte[8]]);

        Assert.That(filter.Apply(block, out _).IsSuccessful, Is.False);
    }

    /// <summary>What it hands on is what it was given: the stage settles no format, it only decides the levels.</summary>
    [Test]
    public void HandsOnTheFormatItWasGiven()
    {
        using ClampAudioFilter filter = new();
        AudioStreamInfo stream = new()
        {
            Index = 0,
            CodecId = "aac",
            SampleRate = SampleRate,
            ChannelCount = 2,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Float32,
        };

        Assert.That(filter.Configure(stream, stream).IsSuccessful, Is.True);
        Assert.That(filter.OutputFormat, Is.SameAs(stream));
    }

    private static RawAudioBuffer CreateBlock(params float[] values)
    {
        return CreatePlanarBlock(AudioSampleFormat.Float32, ChannelLayout.Mono, 1, values.Length, [ToBytes(values)]);
    }

    private static RawAudioBuffer CreatePlanarBlock(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, byte[][] planes)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.FromSeconds(1),
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = new TestPlanarBuffer(planes),
        };
    }

    private static double[] ReadSamples(RawAudioBuffer buffer)
    {
        return ReadPlanes(buffer)[0];
    }

    private static double[][] ReadPlanes(RawAudioBuffer buffer)
    {
        var planar = (IPlanarAudioBuffer)buffer.Buffer;
        double[][] planes = new double[planar.PlaneCount][];
        for (int plane = 0; plane < planar.PlaneCount; plane++)
        {
            int count = planar.GetPlaneByteCount(plane) / sizeof(float);
            float[] values = new float[count];
            Marshal.Copy(planar.GetPlanePointer(plane), values, 0, count);
            planes[plane] = [.. values.Select(value => (double)value)];
        }

        return planes;
    }

    private static byte[] ToBytes(float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed class TestPlanarBuffer : IPlanarAudioBuffer
    {
        private readonly List<nint> _planes = [];
        private readonly List<int> _byteCounts = [];
        private bool _disposed;

        internal TestPlanarBuffer(byte[][] planes)
        {
            foreach (byte[] plane in planes)
            {
                nint memory = Marshal.AllocHGlobal(plane.Length);
                Marshal.Copy(plane, 0, memory, plane.Length);
                _planes.Add(memory);
                _byteCounts.Add(plane.Length);
            }
        }

        public int PlaneCount => _planes.Count;

        public long Length => _byteCounts.Sum();

        public nint GetPlanePointer(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _planes[plane];
        }

        public int GetPlaneByteCount(int plane)
        {
            return _byteCounts[plane];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (nint plane in _planes)
            {
                Marshal.FreeHGlobal(plane);
            }
        }
    }
}
