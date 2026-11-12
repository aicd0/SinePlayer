// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Speed;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The speed filter is the half of playback speed that costs work: a device renders at its own rate, so faster playback means
/// carrying the same media in fewer frames. What the tests pin is that the media the block describes does not change, that the
/// frame count does, that every format survives the round trip through the arithmetic, and that a stream stays continuous
/// across block boundaries rather than restarting at each one.
/// </summary>
[TestFixture]
public class SpeedAudioFilterTests
{
    private const int Frames = 960;

    [Test]
    public void LeavesTheBlockAloneAtItsOwnSpeed()
    {
        using SpeedAudioFilter filter = new();
        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float32, [0.25, -0.25])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, planar, sampleCount: 2, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(output, Is.SameAs(buffer), "nothing is copied or resampled when the speed is one");
        }
    }

    [TestCase(2.0, 480)]
    [TestCase(0.5, 1920)]
    [TestCase(8.0, 120)]
    [TestCase(0.1, 9600)]
    public void CarriesTheMediaInAsManyFramesAsTheSpeedNeeds(double speed, int expectedFrames)
    {
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(speed);

        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float32, [.. Enumerable.Repeat(0.5, Frames)])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, planar, Frames, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(output.SampleCount, Is.EqualTo(expectedFrames));
            Assert.That(output.Timestamp, Is.EqualTo(buffer.Timestamp), "where the media starts is not the speed's business");
            Assert.That(output.SampleRate, Is.EqualTo(buffer.SampleRate));
            Assert.That(output.ChannelCount, Is.EqualTo(buffer.ChannelCount));
            Assert.That(output.ChannelLayout, Is.EqualTo(buffer.ChannelLayout));
            Assert.That(output.SampleFormat, Is.EqualTo(buffer.SampleFormat));
        }
    }

    [TestCase(AudioSampleFormat.UInt8, 192)]
    [TestCase(AudioSampleFormat.Int16, 16384)]
    [TestCase(AudioSampleFormat.Int24, 4194304)]
    [TestCase(AudioSampleFormat.Int32, 1073741824)]
    [TestCase(AudioSampleFormat.Float32, 0.5)]
    [TestCase(AudioSampleFormat.Float64, 0.5)]
    public void KeepsAFormatItUnderstandsIntact(AudioSampleFormat format, double value)
    {
        // A constant signal is what every format has in common: resampled, it has to still be that constant, which is what makes
        // the read and write of each format testable without depending on the interpolation itself.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);

        using TestPlanarBuffer planar = new([Write(format, [.. Enumerable.Repeat(value, Frames)])]);
        using RawAudioBuffer buffer = CreateBuffer(format, planar, Frames, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        Assert.That(result.IsSuccessful, Is.True, result.Message);

        using TestPlanarBuffer read = Read(format, output);
        Assert.That(Read(format, read.Plane(0), output.SampleCount), Is.All.EqualTo(value).Within(1),
            "a constant stays constant through any speed");
    }

    [Test]
    public void AveragesWhatItSkipsWhenFaster()
    {
        // Two frames become one, so the block would lose half of what it carried if the skipped frame were dropped rather than
        // averaged in: the output is the mean of the pair.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);

        double[] ramp = [0.0, 0.5, 0.25, 0.75];
        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, ramp)]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, ramp.Length, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        Assert.That(result.IsSuccessful, Is.True, result.Message);

        using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
        Assert.That(Read(AudioSampleFormat.Float64, read.Plane(0), output.SampleCount), Is.EqualTo(new[] { 0.25, 0.5 }));
    }

    [Test]
    public void BlendsBetweenFramesWhenSlower()
    {
        // Two frames become four, and what lies between them is what is heard: half way is half of the way between them.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(0.5);

        double[] ramp = [0.0, 0.5, 0.25, 0.75];
        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, ramp)]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, ramp.Length, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        Assert.That(result.IsSuccessful, Is.True, result.Message);

        using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
        Assert.That(Read(AudioSampleFormat.Float64, read.Plane(0), 5),
            Is.EqualTo(new[] { 0.0, 0.25, 0.5, 0.375, 0.25 }));
    }

    [TestCase(3.0)]
    [TestCase(0.25)]
    [TestCase(7.5)]
    public void StaysContinuousAcrossManyBlocks(double speed)
    {
        // A steady sound resampled block after block has to stay steady: what is left over from one block is what the next
        // starts at, so nothing drifts and nothing restarts.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(speed);

        double value = 0.25;
        for (int block = 0; block < 40; block++)
        {
            using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, [.. Enumerable.Repeat(value, Frames)])]);
            using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, Frames, channelCount: 1);

            OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);
            Assert.That(result.IsSuccessful, Is.True, result.Message);

            using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
            double[] samples = Read(AudioSampleFormat.Float64, read.Plane(0), output.SampleCount);

            Assert.That(samples, Is.All.EqualTo(value).Within(1e-12), $"block {block} drifted away from the sound it was given");
        }
    }

    [Test]
    public void KeepsTheChannelsApartWhenTheyShareAPlane()
    {
        // What a device consumes: one plane with the channels interleaved. Resampling by frames must not mix them.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);

        double[] interleaved = [1.0, -1.0, 1.0, -1.0, 1.0, -1.0, 1.0, -1.0];
        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, interleaved)]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, sampleCount: 4, channelCount: 2);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(output.SampleCount, Is.EqualTo(2), "two frames of four");
        }

        using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
        Assert.That(Read(AudioSampleFormat.Float64, read.Plane(0), 4), Is.EqualTo(new[] { 1.0, -1.0, 1.0, -1.0 }),
            "each channel keeps its own samples");
    }

    [Test]
    public void KeepsEveryPlaneWhenTheChannelsHaveTheirOwn()
    {
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);

        using TestPlanarBuffer planar = new(
        [
            Write(AudioSampleFormat.Float64, [0.5, 0.5, 0.5, 0.5]),
            Write(AudioSampleFormat.Float64, [-0.5, -0.5, -0.5, -0.5]),
        ]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, sampleCount: 4, channelCount: 2);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        Assert.That(result.IsSuccessful, Is.True, result.Message);

        using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.PlaneCount, Is.EqualTo(2));
            Assert.That(Read(AudioSampleFormat.Float64, read.Plane(0), output.SampleCount), Is.All.EqualTo(0.5));
            Assert.That(Read(AudioSampleFormat.Float64, read.Plane(1), output.SampleCount), Is.All.EqualTo(-0.5),
                "a plane is not resampled from its neighbour");
        }
    }

    [Test]
    public void TreatsAnUnusableSpeedAsItsOwn()
    {
        using SpeedAudioFilter filter = new();

        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, [0.25, 0.25])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, sampleCount: 2, channelCount: 1);

        foreach (double speed in new[] { 0.0, -2.0, double.NaN, double.PositiveInfinity })
        {
            filter.SetSpeed(speed);

            OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);
            Assert.That(result.IsSuccessful, Is.True, result.Message);
            Assert.That(output, Is.SameAs(buffer), $"a speed of {speed} cannot be honoured and changes nothing");
        }
    }

    [Test]
    public void RefusesABlockItCannotRead()
    {
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);

        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, [0.25, 0.25])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Unknown, planar, sampleCount: 2, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False, "a format with no sample size cannot be resampled");
            Assert.That(output, Is.SameAs(buffer), "and the block is handed back as it came in");
        }
    }

    /// <summary>Copies what the filter produced into a buffer of the test's own, so it can be read after the filter's is gone.</summary>
    private static TestPlanarBuffer Read(AudioSampleFormat format, RawAudioBuffer output)
    {
        Assert.That(output.Buffer, Is.AssignableTo<IPlanarAudioBuffer>(), "the filter must produce planes");

        var planes = (IPlanarAudioBuffer)output.Buffer;
        List<byte[]> copied = [];
        for (int plane = 0; plane < planes.PlaneCount; plane++)
        {
            byte[] bytes = new byte[planes.GetPlaneByteCount(plane)];
            Marshal.Copy(planes.GetPlanePointer(plane), bytes, 0, bytes.Length);
            copied.Add(bytes);
        }

        return new TestPlanarBuffer(copied);
    }

    private static int Width(AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
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
            Timestamp = TimeSpan.FromSeconds(1),
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
}
