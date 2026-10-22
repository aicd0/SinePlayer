// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
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

    // Fifty milliseconds: small enough that one block always fits the device buffer, which is what lets the
    // test hand a block over while the device is stopped without waiting for room that is not coming.
    private const int SampleCount = 2400;

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
            Assert.That(renderer.Name, Is.EqualTo("WASAPI"));
            Assert.That(renderer.QueuedSampleCount, Is.EqualTo(0));
            Assert.That(renderer.Clock.IsRunning, Is.False);
            Assert.That(renderer.Clock.Position, Is.EqualTo(TimeSpan.Zero));
        }

        // Whether the stream runs is the pipeline's decision, so a hand-off queues into a stopped device and
        // leaves it stopped: that is what holding the clock back for the picture is made of.
        using RawAudioBuffer buffer = CreateBuffer();
        using (IAudioSubmit submitting = renderer.Submit(buffer))
        {
            Assert.That(submitting.MoveNext(), Is.False, "a stopped device with room takes the whole block at once");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(submitting.Result.IsSuccessful, Is.True, submitting.Result.Message);
                Assert.That(renderer.Clock.IsRunning, Is.False, "a hand-off must not start the device");
                Assert.That(renderer.QueuedSampleCount, Is.GreaterThan(0), "the device holds what it was given");
            }
        }

        // What the pipeline does once it decides that time may pass.
        renderer.Clock.Start();

        using (Assert.EnterMultipleScope())
        {
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

        // A hold is a stopped device that keeps taking audio: the block waits in the device's buffer, and
        // the hand-off leaves the stream stopped instead of restarting it under the picture's feet.
        using (IAudioSubmit held = renderer.Submit(buffer))
        {
            Assert.That(held.MoveNext(), Is.False, "a paused device with room still takes a block");
            Assert.That(held.Result.IsSuccessful, Is.True, held.Result.Message);
            Assert.That(renderer.Clock.IsRunning, Is.False, "a hand-off must not restart a held device");
        }

        Assert.That(renderer.Flush().IsSuccessful, Is.True);
    }

    [Test]
    public void NamesTheDeviceItPlaysThrough()
    {
        using WasapiAudioRenderer auto = new();
        using WasapiAudioRenderer named = new("id", "Speakers");
        using WasapiAudioRenderer namedDefault = new("id", "Speakers", true);

        using (Assert.EnterMultipleScope())
        {
            // The device is named the way a person picks it, and the default one says so.
            Assert.That(auto.Name, Is.EqualTo("WASAPI"));
            Assert.That(named.Name, Is.EqualTo("WASAPI [Speakers]"));
            Assert.That(namedDefault.Name, Is.EqualTo("WASAPI [Speakers] (default)"));
        }
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
    public void SaysItHasNoRoomRatherThanWaiting()
    {
        using WasapiAudioRenderer renderer = new();

        PlaybackOperationResult configured = renderer.Configure(CreateStream());
        if (!configured.IsSuccessful)
        {
            Assert.Ignore($"This machine offers no audio endpoint to test against: {configured.Message}");
        }

        using RawAudioBuffer buffer = CreateBuffer();

        // The stream is left stopped on purpose: a stopped device plays nothing, so its buffer fills without
        // anything having to be raced for, and what a full device does is the whole question.
        using IAudioSubmit handOver = FillTheDevice(renderer, buffer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handOver.Current, Is.EqualTo(AudioSubmitYield.NoRoom));
            Assert.That(renderer.Clock.IsRunning, Is.False, "a full device is not a reason to start the stream");
        }

        // Resetting the device is what gives the room back, which is why the pipeline's wait is the caller's:
        // it is the only party that knows whether the position still wants this block at all.
        Assert.That(renderer.Flush().IsSuccessful, Is.True);
        Assert.That(handOver.MoveNext(), Is.False, "with room again the rest of the block goes over at once");
        Assert.That(handOver.Result.IsSuccessful, Is.True, handOver.Result.Message);
    }

    /// <summary>
    /// Hands blocks over until the device reports that it has no room, and answers the hand-off that said so.
    /// A stopped device does not drain, so this ends; the bound is generous because what is being checked is
    /// that a full device is reported at all, not how many blocks it takes to fill it.
    /// </summary>
    private static IAudioSubmit FillTheDevice(WasapiAudioRenderer renderer, RawAudioBuffer buffer)
    {
        const int maximumBlocks = 60;

        for (int submitted = 0; submitted < maximumBlocks; submitted++)
        {
            IAudioSubmit handOff = renderer.Submit(buffer);
            if (handOff.MoveNext())
            {
                return handOff;
            }

            Assert.That(handOff.Result.IsSuccessful, Is.True, handOff.Result.Message);
            handOff.Dispose();
        }

        throw new InvalidOperationException(
            $"The device took {maximumBlocks} blocks of {buffer.SampleCount} frames without ever reporting that it was full.");
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
