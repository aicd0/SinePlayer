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
    public void DescribesFivePointOneSideWithItsOwnMask()
    {
        // Side speakers and back speakers are different positions, so a device asked to play a 5.1 stream whose last
        // pair is at the sides has to be told that rather than handed the back pair's mask.
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(CreateStream(48000, 6, ChannelLayout.Surround51Side, AudioSampleFormat.Float32));
        WasapiAudioFormat.Descriptor descriptor = described.Result;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.IsSuccessful, Is.True, described.Message);
            Assert.That(descriptor.ChannelMask, Is.EqualTo(0x60F));
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

    [Test]
    public void DescribesTheArrangementsThatAreNotTheCommonOnes()
    {
        // The channel count alone cannot say where the speakers are once a layout is not the common one, so each of
        // these has to reach the device with the mask its own arrangement asserts.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MaskOf(ChannelLayout.Surround70, 7), Is.EqualTo(0x637));
            Assert.That(MaskOf(ChannelLayout.Surround70Front, 7), Is.EqualTo(0x6C7));
            Assert.That(MaskOf(ChannelLayout.Surround61, 7), Is.EqualTo(0x70F));
            Assert.That(MaskOf(ChannelLayout.Surround61Back, 7), Is.EqualTo(0x13F));
            Assert.That(MaskOf(ChannelLayout.Surround61Front, 7), Is.EqualTo(0x6CB));
            Assert.That(MaskOf(ChannelLayout.Surround71Wide, 8), Is.EqualTo(0xFF));
            Assert.That(MaskOf(ChannelLayout.Surround71WideSide, 8), Is.EqualTo(0x6CF));
            Assert.That(MaskOf(ChannelLayout.Surround512, 8), Is.EqualTo(0x560F));
        }
    }

    private static uint MaskOf(ChannelLayout layout, int channelCount)
    {
        OperationResult<WasapiAudioFormat.Descriptor> described = WasapiAudioFormat.TryDescribe(CreateStream(48000, channelCount, layout, AudioSampleFormat.Float32));
        Assert.That(described.IsSuccessful, Is.True, described.Message);
        return described.Result.ChannelMask;
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
