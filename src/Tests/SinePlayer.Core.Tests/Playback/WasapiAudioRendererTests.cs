// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Drives the real default audio endpoint. Everything up to the device hand-off is covered elsewhere;
/// what this adds is that the descriptor is accepted, the device buffer is written, and the clock runs
/// — the last mile, which only a real endpoint can answer for. It skips where there is no device.
/// </summary>
[TestFixture]
public class WasapiAudioRendererTests
{
    private const int SampleRate = 48000;
    private const int ChannelCount = 2;
    private const int SampleCount = 9600;

    [Test]
    public void PlaysAToneThroughTheDefaultDevice()
    {
        using WasapiAudioRenderer renderer = new();

        AudioStreamInfo stream = new()
        {
            Index = 0,
            CodecId = "pcm_s16le",
            SampleRate = SampleRate,
            ChannelCount = ChannelCount,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int16,
        };

        PlaybackOperationResult configured = renderer.Configure(stream);
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine offers no audio endpoint to test against: {configured.Message}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(renderer.Name, Is.EqualTo("wasapi"));
            Assert.That(renderer.QueuedSampleCount, Is.EqualTo(0));
            Assert.That(renderer.Clock.IsRunning, Is.False);
            Assert.That(renderer.Clock.Position, Is.EqualTo(TimeSpan.Zero));
        }

        // A fifth of a second of a quiet tone, which is a fifth of a second of audio played.
        using RawAudioBuffer buffer = CreateBuffer();
        PlaybackOperationResult submitted = renderer.Submit(buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(submitted.IsSuccessful, Is.True, submitted.Message);
            Assert.That(renderer.Clock.IsRunning, Is.True);
            Assert.That(renderer.Clock.Rate, Is.EqualTo(1.0));
        }

        Thread.Sleep(100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(renderer.Clock.Position, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(renderer.Clock.Position, Is.LessThan(TimeSpan.FromSeconds(1)));
        }

        renderer.Clock.Pause();
        TimeSpan paused = renderer.Clock.Position;
        Thread.Sleep(50);
        Assert.That(renderer.Clock.Position, Is.EqualTo(paused), "a stopped device must stop advancing");

        Assert.That(renderer.Flush().IsSuccessful, Is.True);
    }

    [Test]
    public void LeavesEnoughRoomInTheDeviceToAbsorbALateSubmit()
    {
        using WasapiAudioRenderer renderer = new();

        PlaybackOperationResult configured = renderer.Configure(CreateStream());
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine offers no audio endpoint to test against: {configured.Message}");
        }

        // The engine's own default is one period, about ten milliseconds, and a thread handing blocks over
        // can miss that by simply being kept off the processor — which is heard as a click. Whatever the
        // endpoint rounds the request to, it has to leave room for a delay worth caring about.
        double bufferedMilliseconds = renderer.BufferFrameCount * 1000.0 / SampleRate;
        TestContext.Progress.WriteLine($"device buffer: {bufferedMilliseconds:0} ms ({renderer.BufferFrameCount} frames)");
        Assert.That(bufferedMilliseconds, Is.GreaterThanOrEqualTo(100.0), $"the device only holds {bufferedMilliseconds:0} ms of audio");
    }

    [Test]
    public void RefusesAStreamItCannotDescribe()
    {
        using WasapiAudioRenderer renderer = new();

        AudioStreamInfo stream = new()
        {
            Index = 0,
            CodecId = "pcm_s24le",
            SampleRate = SampleRate,
            ChannelCount = ChannelCount,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int24,
        };

        PlaybackOperationResult configured = renderer.Configure(stream);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configured.IsSuccessful, Is.False);
            Assert.That(configured.Message, Does.Contain("24"));
        }
    }

    private static AudioStreamInfo CreateStream()
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "pcm_s16le",
            SampleRate = SampleRate,
            ChannelCount = ChannelCount,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int16,
        };
    }

    private static RawAudioBuffer CreateBuffer()
    {
        byte[] samples = new byte[SampleCount * ChannelCount * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            // Low amplitude noise would be audible; silence is not. A quiet ramp keeps the test honest
            // without being unpleasant to run.
            samples[index] = (byte)((index / 2) % 16);
        }

        return new RawAudioBuffer
        {
            Timestamp = TimeSpan.Zero,
            SampleRate = SampleRate,
            ChannelCount = ChannelCount,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int16,
            SampleCount = SampleCount,
            Buffer = new InterleavedBuffer(samples),
        };
    }

    private sealed class InterleavedBuffer : IPlanarAudioBuffer
    {
        private readonly nint _memory;
        private readonly int _length;

        internal InterleavedBuffer(byte[] samples)
        {
            _length = samples.Length;
            _memory = Marshal.AllocHGlobal(_length);
            Marshal.Copy(samples, 0, _memory, _length);
        }

        public int PlaneCount => 1;

        public long Length => _length;

        public nint GetPlanePointer(int plane)
        {
            return plane == 0 ? _memory : 0;
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane == 0 ? _length : 0;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_memory);
        }
    }
}
