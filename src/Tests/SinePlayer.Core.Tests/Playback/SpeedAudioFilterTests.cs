// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Speed;
using SinePlayer.Core.Playback.Hosting;

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

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
        filter.SetAlgorithm(AudioSpeedAlgorithm.None);

        using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, [0.25, 0.25])]);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Unknown, planar, sampleCount: 2, channelCount: 1);

        OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccessful, Is.False, "a format with no sample size cannot be resampled");
            Assert.That(output, Is.SameAs(buffer), "and the block is handed back as it came in");
        }
    }

    /// <summary>
    /// Stretching is the algorithm that keeps the pitch, and a tone is how that can be seen: the same 440 Hz comes out at 440 Hz
    /// however fast it is played, where resampling takes it to 880 Hz at twice the speed.
    /// </summary>
    [TestCase(AudioSpeedAlgorithm.None, 880.0)]
    [TestCase(AudioSpeedAlgorithm.WSOLA, 440.0)]
    public void LeavesThePitchWhereItIsWhenItStretches(AudioSpeedAlgorithm algorithm, double expected)
    {
        const double frequency = 440.0;
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);
        filter.SetAlgorithm(algorithm);

        List<double> produced = Stretch(Blocks(frequency, 40), filter);

        double measured = FrequencyOf(produced, skip: 8192);
        Assert.That(measured, Is.EqualTo(expected).Within(expected * 0.05),
            "what a tone becomes says which of the two the filter did");
    }

    /// <summary>
    /// Over a run of blocks the frames have to work out to the media over the rate: each window consumed the rate times its own
    /// length, so the count is right on average even though no block on its own is exact.
    /// </summary>
    [TestCase(2.0)]
    [TestCase(0.5)]
    [TestCase(0.25)]
    [TestCase(0.1)]
    [TestCase(4.0)]
    public void CarriesTheMediaInTheLongRunWhenItStretches(double speed)
    {
        const int blocks = 40;
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(speed);
        filter.SetAlgorithm(AudioSpeedAlgorithm.WSOLA);

        List<double> produced = Stretch(Blocks(220.0, blocks), filter);
        double expected = (blocks * Frames) / speed;

        // A window of frames, plus the stretch of audio the search never gets to use.
        double slack = 2 * ((44100 * 20) / 1000 / 2) + ((44100 * 5) / 1000 / speed);
        Assert.That(produced.Count, Is.EqualTo(expected).Within(slack),
            "the frames carried are the media over the rate, in the long run");
    }

    [Test]
    public void KeepsALevelSteadyWhenItStretches()
    {
        // Windows that add up to one leave a constant where it was: a level that drifts or pumps would show here.
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(0.5);
        filter.SetAlgorithm(AudioSpeedAlgorithm.WSOLA);

        List<double> produced = Stretch(Blocks(0.0, 20, level: 0.5), filter);
        double[] settled = [.. produced.Skip(produced.Count / 2)];

        Assert.That(settled, Is.Not.Empty);
        Assert.That(settled, Is.All.EqualTo(0.5).Within(0.02));
    }

    /// <summary>
    /// Below half speed the analysis advances by less than the search reaches back, so the first windows of a stream look for
    /// their predecessors before the history begins. Nothing that is not held may be read - and the sound still has to come out
    /// as what it was.
    /// </summary>
    [TestCase(0.4)]
    [TestCase(0.25)]
    [TestCase(0.1)]
    public void StretchesBelowHalfSpeedWithoutReadingBeforeTheStream(double speed)
    {
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(speed);
        filter.SetAlgorithm(AudioSpeedAlgorithm.WSOLA);

        List<double> produced = Stretch(Blocks(440.0, 10), filter);

        Assert.That(FrequencyOf(produced, skip: 8192), Is.EqualTo(440.0).Within(22.0),
            "the tone is still the tone, however slowly it is stretched");
    }

    [Test]
    public void KeepsEveryPlaneWhenItStretches()
    {
        using SpeedAudioFilter filter = new();
        filter.SetSpeed(2.0);
        filter.SetAlgorithm(AudioSpeedAlgorithm.WSOLA);

        List<double> left = [];
        List<double> right = [];
        for (int block = 0; block < 8; block++)
        {
            using TestPlanarBuffer planar = new(
            [
                Write(AudioSampleFormat.Float64, [.. Enumerable.Repeat(0.5, Frames)]),
                Write(AudioSampleFormat.Float64, [.. Enumerable.Repeat(-0.5, Frames)]),
            ]);
            using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, Frames, channelCount: 2);

            OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);
            Assert.That(result.IsSuccessful, Is.True, result.Message);

            using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
            left.AddRange(Read(AudioSampleFormat.Float64, read.Plane(0), output.SampleCount));
            right.AddRange(Read(AudioSampleFormat.Float64, read.Plane(1), output.SampleCount));
        }

        Assert.That(left.Skip(left.Count / 2), Is.All.EqualTo(0.5).Within(0.02), "each plane keeps its own sound");
        Assert.That(right.Skip(right.Count / 2), Is.All.EqualTo(-0.5).Within(0.02));
    }

    /// <summary>Forty blocks of a tone, block by block, so its phase carries across the boundaries the way a real stream's does.</summary>
    private static List<double[]> Blocks(double frequency, int blocks, double level = 0.5)
    {
        const int sampleRate = 44100;
        List<double[]> all = [];
        for (int block = 0; block < blocks; block++)
        {
            double[] samples = new double[Frames];
            for (int frame = 0; frame < Frames; frame++)
            {
                long at = ((long)block * Frames) + frame;
                samples[frame] = frequency > 0 ? level * Math.Sin(2 * Math.PI * frequency * at / sampleRate) : level;
            }

            all.Add(samples);
        }

        return all;
    }

    /// <summary>Runs blocks through the filter and collects everything it produced, one plane's worth.</summary>
    private static List<double> Stretch(IReadOnlyList<double[]> blocks, SpeedAudioFilter filter)
    {
        List<double> produced = [];
        foreach (double[] block in blocks)
        {
            using TestPlanarBuffer planar = new([Write(AudioSampleFormat.Float64, block)]);
            using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float64, planar, block.Length, channelCount: 1);

            OperationResult result = filter.Apply(buffer, out RawAudioBuffer output);
            Assert.That(result.IsSuccessful, Is.True, result.Message);

            using TestPlanarBuffer read = Read(AudioSampleFormat.Float64, output);
            produced.AddRange(Read(AudioSampleFormat.Float64, read.Plane(0), output.SampleCount));
        }

        return produced;
    }

    /// <summary>
    /// How often the samples cross zero, as a frequency: a tone played faster has more crossings per second, and one stretched
    /// rather than resampled has the same.
    /// </summary>
    private static double FrequencyOf(IReadOnlyList<double> samples, int skip)
    {
        Assert.That(samples.Count, Is.GreaterThan(skip + 1000), "there has to be something to measure");

        int crossings = 0;
        int first = -1;
        int last = -1;
        for (int index = skip; index + 1 < samples.Count; index++)
        {
            double now = samples[index];
            double next = samples[index + 1];
            if ((now < 0 && next >= 0) || (now >= 0 && next < 0))
            {
                if (first < 0)
                {
                    first = index;
                }

                last = index;
                crossings++;
            }
        }

        double seconds = (last - first) / 44100.0;
        Assert.That(seconds, Is.GreaterThan(0));
        return crossings / 2.0 / seconds;
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
