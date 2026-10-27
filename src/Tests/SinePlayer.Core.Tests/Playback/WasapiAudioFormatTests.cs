// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The descriptor built from a stream is what a device is asked to open, so it is verified exactly:
/// speaker masks are the KSAUDIO positions and the byte counts have to line up with the samples the
/// decoder produces.
/// </summary>
[TestFixture]
public class WasapiAudioFormatTests
{
    [Test]
    public void DescribesStereoPcm()
    {
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(CreateStream(44100, 2, ChannelLayout.Stereo, AudioSampleFormat.Int16));
        WasapiAudioFormat.Descriptor descriptor = described.Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.IsSuccessful, Is.True, described.Message);
            Assert.That(descriptor.SampleRate, Is.EqualTo(44100));
            Assert.That(descriptor.ChannelCount, Is.EqualTo(2));
            Assert.That(descriptor.BitsPerSample, Is.EqualTo(16));
            Assert.That(descriptor.BlockAlign, Is.EqualTo(4));
            Assert.That(descriptor.AverageBytesPerSecond, Is.EqualTo(176400));
            Assert.That(descriptor.IsFloat, Is.False);
            Assert.That(descriptor.IsExtensible, Is.False);
            Assert.That(descriptor.ChannelMask, Is.EqualTo(0x3));
        }
    }

    [Test]
    public void DescribesSurroundFloatAsExtensible()
    {
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(CreateStream(48000, 6, ChannelLayout.Surround51, AudioSampleFormat.Float32));
        WasapiAudioFormat.Descriptor descriptor = described.Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.IsSuccessful, Is.True, described.Message);
            Assert.That(descriptor.BlockAlign, Is.EqualTo(24));
            Assert.That(descriptor.AverageBytesPerSecond, Is.EqualTo(1152000));
            Assert.That(descriptor.IsFloat, Is.True);
            Assert.That(descriptor.IsExtensible, Is.True);
            Assert.That(descriptor.ChannelMask, Is.EqualTo(0x3F));
        }
    }

    [Test]
    public void DescribesAFoldDownAsStereo()
    {
        // A surround stream is opened as a stereo stream once the mix has been folded down here, so the device
        // sees two channels and has nothing to convert but the sample rate.
        AudioStreamInfo stream = CreateStream(48000, 6, ChannelLayout.Surround51, AudioSampleFormat.Float32);
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(stream, 2, ChannelLayout.Stereo);
        WasapiAudioFormat.Descriptor descriptor = described.Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.IsSuccessful, Is.True, described.Message);
            Assert.That(descriptor.SampleRate, Is.EqualTo(48000));
            Assert.That(descriptor.ChannelCount, Is.EqualTo(2));
            Assert.That(descriptor.BlockAlign, Is.EqualTo(8));
            Assert.That(descriptor.AverageBytesPerSecond, Is.EqualTo(384000));
            Assert.That(descriptor.IsFloat, Is.True);
            Assert.That(descriptor.IsExtensible, Is.False);
            Assert.That(descriptor.ChannelMask, Is.EqualTo(0x3));
        }
    }

    [Test]
    public void DescribesMonoAsFrontCentre()
    {
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(CreateStream(22050, 1, ChannelLayout.Mono, AudioSampleFormat.UInt8));
        WasapiAudioFormat.Descriptor descriptor = described.Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.IsSuccessful, Is.True, described.Message);
            Assert.That(descriptor.ChannelMask, Is.EqualTo(0x4));
            Assert.That(descriptor.BlockAlign, Is.EqualTo(1));
        }
    }

    [Test]
    public void RefusesFormatsADeviceCannotBeAskedFor()
    {
        using (Assert.EnterMultipleScope())
        {
            OperationResult<WasapiAudioFormat.Descriptor> packed = WasapiAudioFormat.TryDescribe(CreateStream(44100, 2, ChannelLayout.Stereo, AudioSampleFormat.Int24));
            Assert.That(packed.IsSuccessful, Is.False);
            Assert.That(packed.Message, Does.Contain("24"));

            OperationResult<WasapiAudioFormat.Descriptor> unknown = WasapiAudioFormat.TryDescribe(CreateStream(44100, 2, ChannelLayout.Stereo, AudioSampleFormat.Unknown));
            Assert.That(unknown.IsSuccessful, Is.False);
            Assert.That(unknown.Message, Does.Contain("Unknown"));

            // A layout we cannot name would mean deciding which speaker gets which channel.
            OperationResult<WasapiAudioFormat.Descriptor> layout = WasapiAudioFormat.TryDescribe(CreateStream(44100, 6, ChannelLayout.Unknown, AudioSampleFormat.Int16));
            Assert.That(layout.IsSuccessful, Is.False);
            Assert.That(layout.Message, Does.Contain("layout"));

            Assert.That(WasapiAudioFormat.TryDescribe(CreateStream(0, 2, ChannelLayout.Stereo, AudioSampleFormat.Int16)).IsSuccessful, Is.False);
        }
    }

    [Test]
    public void SizesEveryFormatItAccepts()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.UInt8), Is.EqualTo(1));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Int16), Is.EqualTo(2));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Int24), Is.EqualTo(3));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Int32), Is.EqualTo(4));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Float32), Is.EqualTo(4));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Float64), Is.EqualTo(8));
            Assert.That(WasapiAudioFormat.GetBytesPerSample(AudioSampleFormat.Unknown), Is.EqualTo(0));
        }
    }

    private static AudioStreamInfo CreateStream(int sampleRate, int channelCount, ChannelLayout layout, AudioSampleFormat format)
    {
        return new AudioStreamInfo
        {
            Index = 0,
            CodecId = "test",
            SampleRate = sampleRate,
            ChannelCount = channelCount,
            ChannelLayout = layout,
            SampleFormat = format,
        };
    }
}
