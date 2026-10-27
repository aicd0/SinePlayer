// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Downmix;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Folding a surround mix down to two channels is what keeps the level of a film where the file put it: the audio
/// engine's own conversion divides the level down for headroom, and it is precisely the front channels and the
/// centre that suffer. So the coefficients are checked channel by channel, the samples are checked to leave in the
/// format they arrived in, and a stream that already fits is checked to cost nothing.
/// </summary>
[TestFixture]
public class DownmixAudioFilterTests
{
    private const float HalfPower = 0.70710678f;
    private const int SampleRate = 48000;

    [Test]
    public void FoldsFivePointOneWithTheUsualCoefficients()
    {
        // Channels in the order the layout and the channel mask agree on: front pair, centre, low frequency,
        // then the back pair.
        float[][] planes =
        [
            [0.5f],
            [-0.5f],
            [0.2f],
            [0.9f],
            [0.1f],
            [-0.1f],
        ];

        using DownmixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(output.ChannelCount, Is.EqualTo(2));
                Assert.That(output.ChannelLayout, Is.EqualTo(ChannelLayout.Stereo));
                Assert.That(output.SampleCount, Is.EqualTo(1));
                Assert.That(output.Timestamp, Is.EqualTo(buffer.Timestamp));
                Assert.That(ReadSamples(output), Is.EqualTo(new[]
                {
                    0.5 + (HalfPower * 0.2) + (HalfPower * 0.1),
                    -0.5 + (HalfPower * 0.2) - (HalfPower * 0.1),
                }).Within(1e-5));
            }
        }
    }

    [Test]
    public void DropsTheLowFrequencyChannel()
    {
        float[][] planes =
        [
            [0.0f],
            [0.0f],
            [0.0f],
            [0.9f],
            [0.0f],
            [0.0f],
        ];

        using DownmixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 0.0, 0.0 }));
            }
        }
    }

    [Test]
    public void ClampsWhatTheMixPushesPastFullScale()
    {
        float[][] planes =
        [
            [1.0f],
            [1.0f],
            [1.0f],
            [0.0f],
            [1.0f],
            [1.0f],
        ];

        using DownmixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, planes);

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(ReadSamples(output), Is.EqualTo(new[] { 1.0, 1.0 }));
            }
        }
    }

    [Test]
    public void FoldsPlanarInputTheSameWayAsInterleaved()
    {
        // The same four frames of the same five channels, once woven and once one plane per channel: a decoder's
        // output is planar and a device asks for interleaved, so both have to reach the same answer.
        float[,] frames = new float[4, 5];
        for (int frame = 0; frame < 4; frame++)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                frames[frame, channel] = (float)((0.1 * (channel + 1)) - (0.05 * frame));
            }
        }

        float[] interleaved = new float[4 * 5];
        for (int frame = 0; frame < 4; frame++)
        {
            for (int channel = 0; channel < 5; channel++)
            {
                interleaved[(frame * 5) + channel] = frames[frame, channel];
            }
        }

        float[][] planar = new float[5][];
        for (int channel = 0; channel < 5; channel++)
        {
            planar[channel] = new float[4];
            for (int frame = 0; frame < 4; frame++)
            {
                planar[channel][frame] = frames[frame, channel];
            }
        }

        using DownmixAudioFilter filter = Prepare(ChannelLayout.Surround50, 5);
        using RawAudioBuffer woven = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround50, 5, 4, [interleaved]);
        using RawAudioBuffer split = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround50, 5, 4, planar);

        OperationResult fromWoven = filter.Apply(woven, out RawAudioBuffer wovenOutput);
        using (wovenOutput)
        {
            OperationResult fromSplit = filter.Apply(split, out RawAudioBuffer splitOutput);
            using (splitOutput)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(fromWoven.IsSuccessful, Is.True, fromWoven.Message);
                    Assert.That(fromSplit.IsSuccessful, Is.True, fromSplit.Message);
                    Assert.That(ReadSamples(wovenOutput), Is.EqualTo(ReadSamples(splitOutput)).Within(1e-6));
                }
            }
        }
    }

    [Test]
    public void KeepsTheSampleFormatItWasHanded()
    {
        // A block of 16 bit samples folds into 16 bit samples: the renderer opened the device for the format the
        // decoder produced, and the filter is not the place that changes it.
        short[][] planes =
        [
            [16000],
            [-16000],
            [0],
            [0],
            [0],
            [0],
        ];

        using DownmixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, ChannelLayout.Surround51, 6, 1, ToBytes(planes));

        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);
        using (output)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied.IsSuccessful, Is.True, applied.Message);
                Assert.That(output.SampleFormat, Is.EqualTo(AudioSampleFormat.Int16));
                Assert.That(output.Buffer, Is.AssignableTo<IPlanarAudioBuffer>());
                Assert.That(((IPlanarAudioBuffer)output.Buffer).GetPlaneByteCount(0), Is.EqualTo(sizeof(short) * 2));
            }
        }
    }

    [Test]
    public void LeavesAStreamThatAlreadyFitsAlone()
    {
        using DownmixAudioFilter filter = new();
        AudioStreamInfo stereo = CreateStream(ChannelLayout.Stereo, 2);
        OperationResult configured = filter.Configure(stereo, stereo);

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Stereo, 2, 2, [[0.1f, 0.2f], [0.3f, 0.4f]]);
        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configured.IsSuccessful, Is.True, configured.Message);
            Assert.That(filter.OutputFormat, Is.Null);
            Assert.That(applied.IsSuccessful, Is.True, applied.Message);
            Assert.That(output, Is.SameAs(buffer), "a block with nothing to fold is handed straight back");
        }
    }

    [Test]
    public void RefusesALayoutItHasNoPositionsFor()
    {
        using DownmixAudioFilter filter = new();
        AudioStreamInfo unknown = CreateStream(ChannelLayout.Unknown, 6);
        OperationResult configured = filter.Configure(unknown, unknown.WithLayout(2, ChannelLayout.Stereo));

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Unknown, 6, 1, [[0.0f], [0.0f], [0.0f], [0.0f], [0.0f], [0.0f]]);
        OperationResult applied = filter.Apply(buffer, out RawAudioBuffer output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configured.IsSuccessful, Is.False);
            Assert.That(configured.Message, Does.Contain("no fold down"));
            Assert.That(filter.OutputFormat, Is.Null);
            Assert.That(applied.IsSuccessful, Is.True);
            Assert.That(output, Is.SameAs(buffer));
        }
    }

    [Test]
    public void RefusesABlockAfterBeingDisposed()
    {
        DownmixAudioFilter filter = Prepare(ChannelLayout.Surround51, 6);
        filter.Dispose();

        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, ChannelLayout.Surround51, 6, 1, [[0.0f], [0.0f], [0.0f], [0.0f], [0.0f], [0.0f]]);
        OperationResult applied = filter.Apply(buffer, out _);

        Assert.That(applied.IsSuccessful, Is.False);
    }

    private static DownmixAudioFilter Prepare(ChannelLayout layout, int channelCount, int targetChannelCount = 2)
    {
        DownmixAudioFilter filter = new();
        AudioStreamInfo stream = CreateStream(layout, channelCount);
        AudioStreamInfo target = stream.WithLayout(targetChannelCount, targetChannelCount == 2 ? ChannelLayout.Stereo : ChannelLayout.Mono);
        OperationResult configured = filter.Configure(stream, target);
        Assert.That(configured.IsSuccessful, Is.True, configured.Message);
        return filter;
    }

    private static AudioStreamInfo CreateStream(ChannelLayout layout, int channelCount)
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "aac",
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = AudioSampleFormat.Float32,
        };
    }

    private static double[] ReadSamples(RawAudioBuffer buffer)
    {
        var planar = (IPlanarAudioBuffer)buffer.Buffer;
        nint pointer = planar.GetPlanePointer(0);
        int count = planar.GetPlaneByteCount(0) / sizeof(float);
        float[] values = new float[count];
        Marshal.Copy(pointer, values, 0, count);
        return [.. values.Select(value => (double)value)];
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, IReadOnlyList<float[]> planes)
    {
        return CreateBuffer(format, layout, channelCount, sampleCount, ToBytes(planes));
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, ChannelLayout layout, int channelCount, int sampleCount, IReadOnlyList<byte[]> planes)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.FromSeconds(3),
            SampleRate = SampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = new TestPlanarBuffer(planes),
        };
    }

    private static byte[][] ToBytes(IReadOnlyList<float[]> planes)
    {
        byte[][] bytes = new byte[planes.Count][];
        for (int plane = 0; plane < planes.Count; plane++)
        {
            bytes[plane] = new byte[planes[plane].Length * sizeof(float)];
            Buffer.BlockCopy(planes[plane], 0, bytes[plane], 0, bytes[plane].Length);
        }

        return bytes;
    }

    private static byte[][] ToBytes(IReadOnlyList<short[]> planes)
    {
        byte[][] bytes = new byte[planes.Count][];
        for (int plane = 0; plane < planes.Count; plane++)
        {
            bytes[plane] = new byte[planes[plane].Length * sizeof(short)];
            Buffer.BlockCopy(planes[plane], 0, bytes[plane], 0, bytes[plane].Length);
        }

        return bytes;
    }

    private sealed class TestPlanarBuffer : IPlanarAudioBuffer
    {
        private readonly List<nint> _planes = [];
        private readonly List<int> _byteCounts = [];

        internal TestPlanarBuffer(IReadOnlyList<byte[]> planes)
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

        public long Length => 0;

        public nint GetPlanePointer(int plane)
        {
            return plane >= 0 && plane < _planes.Count ? _planes[plane] : 0;
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane >= 0 && plane < _byteCounts.Count ? _byteCounts[plane] : 0;
        }

        public void Dispose()
        {
            foreach (nint plane in _planes)
            {
                Marshal.FreeHGlobal(plane);
            }

            _planes.Clear();
            _byteCounts.Clear();
        }
    }
}