// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Conversion;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The first stage of the audio chain settles the one format everything after it reads: planar 32 bit floats. What a
/// block arrives as comes from the block, never from the stream's declaration — a file may declare a format the
/// decoder does not produce — and no sample is clamped on the way, so a peak above full scale survives to the stage
/// that decides what to do with it.
/// </summary>
[TestFixture]
public class ConversionAudioFilterTests
{
    private const int SampleRate = 48000;

    /// <summary>
    /// An 8, 16, 24 or 32 bit integer and a 64 bit float all become levels on the same scale, with full scale at one.
    /// The values are the ones a decoder produces at full scale, silence and the negative peak.
    /// </summary>
    [Test]
    public void ConvertsEveryFormatToLevels([Values(AudioSampleFormat.UInt8, AudioSampleFormat.Int16, AudioSampleFormat.Int24, AudioSampleFormat.Int32, AudioSampleFormat.Float64)] AudioSampleFormat format)
    {
        double[][] expected = [[0.5, 0.0, -0.5]];
        byte[] plane = ToBytes(format, [.. expected[0].Select(value => (float)value)]);

        using ConversionAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(format, ChannelLayout.Mono, 1, expected[0].Length, [plane]);
        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (output)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.SampleFormat, Is.EqualTo(AudioSampleFormat.Float32));
            Assert.That(output.SampleCount, Is.EqualTo(expected[0].Length));
            Assert.That(output.Timestamp, Is.EqualTo(block.Timestamp), "the block keeps where it belongs in the stream");
            Assert.That(output.SampleRate, Is.EqualTo(SampleRate));
            Assert.That(ReadPlanes(output), Is.EqualTo(expected).Within(Int32ToFloatTolerance));
        }
    }

    /// <summary>
    /// A block that is already in the chain's format is handed back as it is rather than copied: most decoders
    /// already give floats, and converting those would be a copy that changes nothing.
    /// </summary>
    [Test]
    public void HandsBackABlockThatIsAlreadyFloat()
    {
        using ConversionAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(AudioSampleFormat.Float32, ChannelLayout.Mono, 1, 2, [ToBytes(AudioSampleFormat.Float32, [0.25f, -0.25f])]);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);
        Assert.That(output, Is.SameAs(block), "nothing had to be converted");
    }

    /// <summary>
    /// An interleaved block has its channels woven apart: the float block that comes out is planar, one plane per
    /// channel, because that is what the stages after this one walk.
    /// </summary>
    [Test]
    public void WritesAPlaneForEachChannelOfAnInterleavedBlock()
    {
        short[] frames = [1000, -1000, 2000, -2000];
        using ConversionAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(AudioSampleFormat.Int16, ChannelLayout.Stereo, 2, 2, [ToBytes(frames)]);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (output)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((IPlanarAudioBuffer)output.Buffer).PlaneCount, Is.EqualTo(2));
            Assert.That(ReadPlanes(output)[0], Is.EqualTo(new[] { 1000 / 32768.0, 2000 / 32768.0 }).Within(Int32ToFloatTolerance));
            Assert.That(ReadPlanes(output)[1], Is.EqualTo(new[] { -1000 / 32768.0, -2000 / 32768.0 }).Within(Int32ToFloatTolerance));
        }
    }

    /// <summary>
    /// A sample above full scale is not clipped here: the whole point of settling the format once is that what a later
    /// stage does with a peak is decided in one place, not in every stage it passes.
    /// </summary>
    [Test]
    public void KeepsASampleAboveFullScale()
    {
        using ConversionAudioFilter filter = new();
        using RawAudioBuffer block = CreateBlock(AudioSampleFormat.Float64, ChannelLayout.Mono, 1, 2, [ToBytes(AudioSampleFormat.Float64, [1.5f, -2.0f])]);

        Assert.That(filter.Apply(block, out RawAudioBuffer output).IsSuccessful, Is.True);

        using (output)
        {
            Assert.That(ReadPlanes(output)[0], Is.EqualTo(new[] { 1.5, -2.0 }).Within(Int32ToFloatTolerance));
        }
    }

    /// <summary>
    /// What the block says it holds is what it is read as, whatever the stream declared: a block claiming a format
    /// this filter cannot read fails, and one whose planes cannot hold its samples fails too.
    /// </summary>
    [Test]
    public void RefusesABlockItCannotRead()
    {
        using ConversionAudioFilter filter = new();
        using RawAudioBuffer unknown = CreateBlock(AudioSampleFormat.Unknown, ChannelLayout.Mono, 1, 2, [new byte[8]]);
        using RawAudioBuffer shortPlane = CreateBlock(AudioSampleFormat.Int16, ChannelLayout.Mono, 1, 4, [new byte[4]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Apply(unknown, out _).IsSuccessful, Is.False);
            OperationResult applied = filter.Apply(shortPlane, out _);
            Assert.That(applied.IsSuccessful, Is.False);
            Assert.That(applied.Message, Does.Contain("Plane 0"));
        }
    }

    /// <summary>
    /// The chain is prepared for what this stage hands on, and that is floats whatever the stream declares — including
    /// when the declaration says nothing at all, which must not stop the chain from being prepared.
    /// </summary>
    [Test]
    public void DeclaresFloatsWhateverTheStreamDeclares([Values] AudioSampleFormat declared)
    {
        using ConversionAudioFilter filter = new();
        AudioStreamInfo stream = CreateStream(declared);

        Assert.That(filter.Configure(stream, stream).IsSuccessful, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.OutputFormat?.SampleFormat, Is.EqualTo(AudioSampleFormat.Float32));
            Assert.That(filter.OutputFormat?.ChannelCount, Is.EqualTo(stream.ChannelCount), "the shape is the stream's");
            Assert.That(filter.OutputFormat?.SampleRate, Is.EqualTo(stream.SampleRate));
        }
    }

    private const double Int32ToFloatTolerance = 1.0 / 8388608.0;

    private static AudioStreamInfo CreateStream(AudioSampleFormat format)
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "aac",
            SampleRate = SampleRate,
            ChannelCount = 2,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = format,
        };
    }

    private static RawAudioBuffer CreateBlock(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, byte[][] planes)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.FromSeconds(2),
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = new TestPlanarBuffer(planes),
        };
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

    private static byte[] ToBytes(AudioSampleFormat format, float[] levels)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => [.. levels.Select(value => (byte)Math.Round((value * 128.0) + 128.0))],
            AudioSampleFormat.Int16 => ToInt16(levels),
            AudioSampleFormat.Int24 => ToInt24(levels),
            AudioSampleFormat.Int32 => ToInt32(levels),
            AudioSampleFormat.Float32 => ToFloat32(levels),
            AudioSampleFormat.Float64 => ToFloat64(levels),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a format these fixtures write."),
        };
    }

    private static byte[] ToInt16(float[] levels)
    {
        short[] values = [.. levels.Select(value => (short)Math.Round(value * 32768.0))];
        return ToBytes(values);
    }

    private static byte[] ToBytes(short[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(short)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToInt32(float[] levels)
    {
        int[] values = [.. levels.Select(value => (int)Math.Round(value * 2147483648.0))];
        byte[] bytes = new byte[values.Length * sizeof(int)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToFloat32(float[] levels)
    {
        byte[] bytes = new byte[levels.Length * sizeof(float)];
        Buffer.BlockCopy(levels, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToFloat64(float[] levels)
    {
        double[] values = [.. levels.Select(value => (double)value)];
        byte[] bytes = new byte[values.Length * sizeof(double)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] ToInt24(float[] levels)
    {
        byte[] bytes = new byte[levels.Length * 3];
        for (int index = 0; index < levels.Length; index++)
        {
            int value = (int)Math.Round(levels[index] * 8388608.0);
            bytes[(index * 3) + 0] = (byte)value;
            bytes[(index * 3) + 1] = (byte)(value >> 8);
            bytes[(index * 3) + 2] = (byte)(value >> 16);
        }

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
