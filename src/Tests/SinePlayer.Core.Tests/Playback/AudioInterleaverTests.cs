// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Interleaving is the only conversion the renderer performs itself, so it is checked byte for byte:
/// a planar block has to come out in the order a device reads it, and interleaved input has to survive
/// untouched.
/// </summary>
[TestFixture]
public class AudioInterleaverTests
{
    [Test]
    public void CopiesInterleavedSamplesUnchanged()
    {
        byte[] source = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, 2, 2, [source]);
        byte[] destination = new byte[source.Length];

        bool interleaved = AudioInterleaver.TryInterleave(buffer, destination, out int byteCount, out string error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interleaved, Is.True, error);
            Assert.That(byteCount, Is.EqualTo(source.Length));
            Assert.That(destination, Is.EqualTo(source));
        }
    }

    [Test]
    public void WeavesPlanarChannelsInSampleOrder()
    {
        // Two channels of float samples, laid out one channel per plane.
        byte[] left = [0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22];
        byte[] right = [0x33, 0x33, 0x33, 0x33, 0x44, 0x44, 0x44, 0x44];
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Float32, 2, 2, [left, right]);
        byte[] destination = new byte[left.Length + right.Length];

        bool interleaved = AudioInterleaver.TryInterleave(buffer, destination, out int byteCount, out string error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interleaved, Is.True, error);
            Assert.That(byteCount, Is.EqualTo(destination.Length));
            Assert.That(destination, Is.EqualTo(new byte[]
            {
                0x11, 0x11, 0x11, 0x11, 0x33, 0x33, 0x33, 0x33,
                0x22, 0x22, 0x22, 0x22, 0x44, 0x44, 0x44, 0x44,
            }));
        }
    }

    [Test]
    public void RefusesWhatItCannotLayOut()
    {
        byte[] samples = new byte[8];
        using RawAudioBuffer buffer = CreateBuffer(AudioSampleFormat.Int16, 2, 2, [samples]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AudioInterleaver.TryInterleave(buffer, new byte[4], out _, out string small), Is.False);
            Assert.That(small, Does.Contain("needed"));

            using RawAudioBuffer mismatched = CreateBuffer(AudioSampleFormat.Int16, 4, 2, [new byte[16], new byte[16]]);
            Assert.That(AudioInterleaver.TryInterleave(mismatched, new byte[16], out _, out string planes), Is.False);
            Assert.That(planes, Does.Contain("plane"));
        }
    }

    private static RawAudioBuffer CreateBuffer(AudioSampleFormat format, int channelCount, int sampleCount, IReadOnlyList<byte[]> planes)
    {
        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = 44100,
            ChannelCount = channelCount,
            ChannelLayout = channelCount == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo,
            SampleFormat = format,
            SampleCount = sampleCount,
            Buffer = new TestPlanarBuffer(planes),
        };
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
