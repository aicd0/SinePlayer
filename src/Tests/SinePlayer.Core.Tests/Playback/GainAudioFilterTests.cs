// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Gain;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The gain filter is checked byte for byte, because "the level changed" is only true if the samples changed by
/// exactly the level: the sign has to survive, an integer has to round and saturate in its own format, and an
/// unsigned byte has to be scaled around its midpoint rather than from zero.
/// </summary>
[TestFixture]
public class GainAudioFilterTests
{
    [TestCase(AudioSampleFormat.UInt8, new double[] { 0, 64, 128, 192, 255 }, new double[] { 64, 96, 128, 160, 192 })]
    [TestCase(AudioSampleFormat.Int16, new double[] { -32768, -16384, 0, 16384, 32767 }, new double[] { -16384, -8192, 0, 8192, 16384 })]
    [TestCase(AudioSampleFormat.Int24, new double[] { -8388608, -4194304, 0, 4194304, 8388607 }, new double[] { -4194304, -2097152, 0, 2097152, 4194304 })]
    [TestCase(AudioSampleFormat.Int32, new double[] { -2147483648, -1073741824, 0, 1073741824, 2147483647 }, new double[] { -1073741824, -536870912, 0, 536870912, 1073741824 })]
    [TestCase(AudioSampleFormat.Float32, new double[] { -1, -0.5, 0, 0.5, 1 }, new double[] { -0.5, -0.25, 0, 0.25, 0.5 })]
    [TestCase(AudioSampleFormat.Float64, new double[] { -1, -0.5, 0, 0.5, 1 }, new double[] { -0.5, -0.25, 0, 0.25, 0.5 })]
    public void ScalesEverySampleFormatByTheLevel(AudioSampleFormat format, double[] samples, double[] expected)
    {
        double[] other = [.. samples.Reverse()];
        double[] expectedOther = [.. expected.Reverse()];

        using GainAudioFilter filter = new();
        filter.SetLevel(0.5, false);

        // One plane per channel, which is what a decoder usually produces.
        using (TestPlanarBuffer planar = new([Write(format, samples), Write(format, other)]))
        using (RawAudioBuffer buffer = CreateBuffer(format, planar, samples.Length, channelCount: 2))
        {
            PlaybackOperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.IsSuccessful, Is.True, result.Message);
                Assert.That(output, Is.SameAs(buffer), "an in-place filter hands back the block it was given");
                Assert.That(Read(format, planar.Plane(0), samples.Length), Is.EqualTo(expected));
                Assert.That(Read(format, planar.Plane(1), samples.Length), Is.EqualTo(expectedOther));
            }
        }

        // Every channel in one plane, which is what a device consumes.
        double[] interleaved = new double[samples.Length * 2];
        double[] expectedInterleaved = new double[expected.Length * 2];
        for (int index = 0; index < samples.Length; index++)
        {
            interleaved[index * 2] = samples[index];
            interleaved[(index * 2) + 1] = other[index];
            expectedInterleaved[index * 2] = expected[index];
            expectedInterleaved[(index * 2) + 1] = expectedOther[index];
        }

        using (TestPlanarBuffer planar = new([Write(format, interleaved)]))
        using (RawAudioBuffer buffer = CreateBuffer(format, planar, samples.Length, channelCount: 2))
        {
            PlaybackOperationResult result = filter.Apply(buffer, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.IsSuccessful, Is.True, result.Message);
                Assert.That(Read(format, planar.Plane(0), interleaved.Length), Is.EqualTo(expectedInterleaved));
            }
        }
    }

    [TestCase(AudioSampleFormat.UInt8, 128)]
    [TestCase(AudioSampleFormat.Int16, 0)]
    [TestCase(AudioSampleFormat.Int24, 0)]
    [TestCase(AudioSampleFormat.Int32, 0)]
    [TestCase(AudioSampleFormat.Float32, 0)]
    [TestCase(AudioSampleFormat.Float64, 0)]
    public void MutesBySilencingTheBlockAndRestoresTheLevelAfterwards(AudioSampleFormat format, double silence)
    {
        double[] samples = FullScale(format);
        double[] silenced = [.. samples.Select(_ => silence)];

        using GainAudioFilter filter = new();
        filter.SetLevel(1.0, muted: true);

        using (TestPlanarBuffer planar = new([Write(format, samples)]))
        using (RawAudioBuffer buffer = CreateBuffer(format, planar, samples.Length, channelCount: 1))
        {
            Assert.That(filter.Apply(buffer, out _).IsSuccessful, Is.True);
            Assert.That(Read(format, planar.Plane(0), samples.Length), Is.EqualTo(silenced), "a muted block is the format's own silence");
        }

        // What was already silenced stays silent; the level is what unmuting restores, for the blocks that follow.
        filter.SetLevel(1.0, muted: false);

        using (TestPlanarBuffer planar = new([Write(format, samples)]))
        using (RawAudioBuffer buffer = CreateBuffer(format, planar, samples.Length, channelCount: 1))
        {
            byte[] before = [.. planar.Plane(0)];

            Assert.That(filter.Apply(buffer, out _).IsSuccessful, Is.True);
            Assert.That(planar.Plane(0), Is.EqualTo(before), "at full level a block is left exactly as it was");
        }
    }

    /// <summary>
    /// The default level is the one that changes nothing, which is why a filter nobody set has no cost: not a
    /// byte is written, and the caller gets its own instance back rather than a copy.
    /// </summary>
    [Test]
    public void LeavesAFullLevelBlockExactlyAsItWas()
    {
        double[] samples = [-32768, -12345, 0, 12345, 32767];
        byte[] original = Write(AudioSampleFormat.Int16, samples);

        using GainAudioFilter filter = new();
        using TestPlanarBuffer planar = new([original]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, planar, samples.Length, channelCount: 1);

        PlaybackOperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(output, Is.SameAs(buffer), "a pass-through is the same block, not a copy of it");
            Assert.That(planar.Plane(0), Is.EqualTo(original));
        }
    }

    [Test]
    public void RefusesABufferThatDoesNotExposeItsPlanes()
    {
        using GainAudioFilter filter = new();
        filter.SetLevel(0.5, false);

        using RawAudioBuffer buffer = new()
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = 2,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int16,
            SampleCount = 4,
            Buffer = new OpaqueBuffer(16),
        };

        PlaybackOperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False);
            Assert.That(result.Message, Does.Contain("planes"));
            Assert.That(output, Is.SameAs(buffer), "what could not be transformed is handed on unchanged");
        }
    }

    /// <summary>
    /// A host may still hold the filter a rebuild replaced, so a level that arrives after disposal must be
    /// harmless rather than an exception in whichever thread the host was using.
    /// </summary>
    [Test]
    public void AcceptsALevelAfterItHasBeenDisposed()
    {
        GainAudioFilter filter = new();
        filter.Dispose();

        Assert.DoesNotThrow(() => filter.SetLevel(0.5, false));

        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Int16, [100, 200])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, planar, 2, channelCount: 1);

        Assert.That(filter.Apply(buffer, out _).IsSuccessful, Is.False, "a disposed filter says so rather than touching the block");
    }

    /// <summary>
    /// The level is written by the host and read by the audio thread, which is the one cross-thread edge in the
    /// engine's audio path. Every block must be scaled by one level or by the other — a torn or half-applied
    /// level would be heard as a click or a jump.
    /// </summary>
    [Test]
    public void TakesALevelFromAnotherThreadWhileApplying()
    {
        using GainAudioFilter filter = new();
        filter.SetLevel(0.5, false);

        using ManualResetEventSlim stop = new(false);
        Thread writer = new(() =>
        {
            double level = 0.5;
            while (!stop.IsSet)
            {
                filter.SetLevel(level, false);
                level = level == 0.5 ? 0.25 : 0.5;
            }
        })
        {
            IsBackground = true,
            Name = "Gain filter level",
        };

        writer.Start();

        try
        {
            double[] samples = [-32768, -12345, 0, 12345, 32767];
            double[] half = [.. samples.Select(value => Math.Round(value * 0.5))];
            double[] quarter = [.. samples.Select(value => Math.Round(value * 0.25))];

            for (int iteration = 0; iteration < 200; iteration++)
            {
                using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Int16, samples)]);
                using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, planar, samples.Length, channelCount: 1);

                Assert.That(filter.Apply(buffer, out _).IsSuccessful, Is.True);
                Assert.That(
                    Read(AudioSampleFormat.Int16, planar.Plane(0), samples.Length),
                    Is.EqualTo(half).Or.EqualTo(quarter),
                    "a block is scaled by one level or the other, never by a mixture");
            }
        }
        finally
        {
            stop.Set();
            writer.Join();
        }
    }

    private static double[] FullScale(AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => [0, 64, 128, 192, 255],
            AudioSampleFormat.Int16 => [-32768, -16384, 0, 16384, 32767],
            AudioSampleFormat.Int24 => [-8388608, -4194304, 0, 4194304, 8388607],
            AudioSampleFormat.Int32 => [-2147483648, -1073741824, 0, 1073741824, 2147483647],
            _ => [-1, -0.5, 0, 0.5, 1],
        };
    }

    private static int Width(AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 or AudioSampleFormat.Float32 => 4,
            AudioSampleFormat.Float64 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No test case uses this format."),
        };
    }

    private static byte[] Write(AudioSampleFormat format, IReadOnlyList<double> values)
    {
        int width = Width(format);
        byte[] bytes = new byte[width * values.Count];

        for (int index = 0; index < values.Count; index++)
        {
            int offset = index * width;
            switch (format)
            {
                case AudioSampleFormat.UInt8:
                    bytes[offset] = (byte)values[index];
                    break;

                case AudioSampleFormat.Int16:
                    short sample16 = (short)values[index];
                    bytes[offset] = (byte)sample16;
                    bytes[offset + 1] = (byte)(sample16 >> 8);
                    break;

                case AudioSampleFormat.Int24:
                    int sample24 = (int)values[index];
                    bytes[offset] = (byte)sample24;
                    bytes[offset + 1] = (byte)(sample24 >> 8);
                    bytes[offset + 2] = (byte)(sample24 >> 16);
                    break;

                case AudioSampleFormat.Int32:
                    int sample32 = (int)values[index];
                    bytes[offset] = (byte)sample32;
                    bytes[offset + 1] = (byte)(sample32 >> 8);
                    bytes[offset + 2] = (byte)(sample32 >> 16);
                    bytes[offset + 3] = (byte)(sample32 >> 24);
                    break;

                case AudioSampleFormat.Float32:
                    BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), (float)values[index]);
                    break;

                case AudioSampleFormat.Float64:
                    BitConverter.TryWriteBytes(bytes.AsSpan(offset, 8), values[index]);
                    break;
            }
        }

        return bytes;
    }

    private static double[] Read(AudioSampleFormat format, byte[] plane, int count)
    {
        int width = Width(format);
        double[] values = new double[count];

        for (int index = 0; index < count; index++)
        {
            int offset = index * width;
            values[index] = format switch
            {
                AudioSampleFormat.UInt8 => plane[offset],
                AudioSampleFormat.Int16 => (short)(plane[offset] | (plane[offset + 1] << 8)),
                AudioSampleFormat.Int24 => ReadInt24(plane, offset),
                AudioSampleFormat.Int32 => BitConverter.ToInt32(plane, offset),
                AudioSampleFormat.Float32 => BitConverter.ToSingle(plane, offset),
                AudioSampleFormat.Float64 => BitConverter.ToDouble(plane, offset),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No test case uses this format."),
            };
        }

        return values;
    }

    private static int ReadInt24(byte[] plane, int offset)
    {
        int value = plane[offset] | (plane[offset + 1] << 8) | (plane[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | ~0xFFFFFF : value;
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, IPlanarAudioBuffer buffer, int sampleCount, int channelCount)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = channelCount,
            ChannelLayout = channelCount == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = buffer,
        };
    }

    private sealed class TestPlanarBuffer : IPlanarAudioBuffer
    {
        private readonly List<byte[]> _planes;
        private readonly List<GCHandle> _handles = [];
        private bool _disposed;

        internal TestPlanarBuffer(IReadOnlyList<byte[]> planes)
        {
            _planes = [.. planes];
            foreach (byte[] plane in _planes)
            {
                _handles.Add(GCHandle.Alloc(plane, GCHandleType.Pinned));
            }
        }

        public int PlaneCount => _planes.Count;

        public long Length => _planes.Sum(plane => (long)plane.Length);

        internal byte[] Plane(int index)
        {
            return _planes[index];
        }

        public nint GetPlanePointer(int plane)
        {
            return plane >= 0 && plane < _planes.Count ? _handles[plane].AddrOfPinnedObject() : 0;
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane >= 0 && plane < _planes.Count ? _planes[plane].Length : 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (GCHandle handle in _handles)
            {
                handle.Free();
            }

            _handles.Clear();
        }
    }

    private sealed class OpaqueBuffer(long length) : IMediaBuffer
    {
        public long Length => length;

        public void Dispose()
        {
        }
    }
}
