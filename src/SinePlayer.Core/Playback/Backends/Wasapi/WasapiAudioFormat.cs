// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// Describes a decoded stream as a WASAPI format descriptor. The mapping is a pure function so it can
/// be verified without a device, which matters because a wrong descriptor is the most common reason a
/// stream fails to open.
/// <para>
/// Channel masks are the KSAUDIO speaker positions, which is what both <c>WAVEFORMATEXTENSIBLE</c> and
/// the audio engine use to decide how channels map onto speakers.
/// </para>
/// </summary>
internal static class WasapiAudioFormat
{
    private const uint SpeakerFrontLeft = 0x1;
    private const uint SpeakerFrontRight = 0x2;
    private const uint SpeakerFrontCenter = 0x4;
    private const uint SpeakerLowFrequency = 0x8;
    private const uint SpeakerBackLeft = 0x10;
    private const uint SpeakerBackRight = 0x20;
    private const uint SpeakerSideLeft = 0x200;
    private const uint SpeakerSideRight = 0x400;

    /// <summary>Whatever the device decides, which is what a stream with no declared layout asks for.</summary>
    internal const uint ChannelMaskUnspecified = 0;

    internal readonly struct Descriptor
    {
        public required int SampleRate { get; init; }

        public required int ChannelCount { get; init; }

        public required int BitsPerSample { get; init; }

        /// <summary>Bytes one frame of every channel occupies.</summary>
        public required int BlockAlign { get; init; }

        public required int AverageBytesPerSecond { get; init; }

        public required bool IsFloat { get; init; }

        /// <summary>Whether the stream needs the extensible descriptor to carry a channel mask.</summary>
        public required bool IsExtensible { get; init; }

        public required uint ChannelMask { get; init; }
    }

    /// <summary>
    /// Bytes one sample of a format occupies. Kept here so format description and interleaving read
    /// from one table rather than each deciding for itself.
    /// </summary>
    internal static int GetBytesPerSample(AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
            AudioSampleFormat.Float64 => 8,
            _ => 0,
        };
    }

    internal static bool TryDescribe(AudioStreamInfo stream, out Descriptor descriptor, out string error)
    {
        descriptor = default;

        if (stream.SampleRate <= 0)
        {
            error = "The stream declares no sample rate.";
            return false;
        }

        if (stream.ChannelCount <= 0)
        {
            error = "The stream declares no channels.";
            return false;
        }

        int bitsPerSample = GetBitsPerSample(stream.SampleFormat);
        if (bitsPerSample == 0)
        {
            error = stream.SampleFormat == AudioSampleFormat.Int24
                ? "A 24 bit stream is not described in the 24-in-32 layout WASAPI expects."
                : $"The sample format '{stream.SampleFormat}' has no WASAPI equivalent.";
            return false;
        }

        uint channelMask = GetChannelMask(stream.ChannelLayout);
        bool isExtensible = stream.ChannelCount > 2;
        if (isExtensible && channelMask == ChannelMaskUnspecified)
        {
            // Guessing positions for a layout we cannot name would decide for the device which speaker
            // gets which channel. Refusing is more honest than producing a plausible lie.
            error = $"No speaker layout is known for {stream.ChannelCount} channels of layout '{stream.ChannelLayout}'.";
            return false;
        }

        int blockAlign = (bitsPerSample / 8) * stream.ChannelCount;
        descriptor = new Descriptor
        {
            SampleRate = stream.SampleRate,
            ChannelCount = stream.ChannelCount,
            BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign,
            AverageBytesPerSecond = blockAlign * stream.SampleRate,
            IsFloat = stream.SampleFormat is AudioSampleFormat.Float32 or AudioSampleFormat.Float64,
            IsExtensible = isExtensible,
            ChannelMask = channelMask,
        };
        error = string.Empty;
        return true;
    }

    private static int GetBitsPerSample(AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => 8,
            AudioSampleFormat.Int16 => 16,
            AudioSampleFormat.Int32 => 32,
            AudioSampleFormat.Float32 => 32,
            AudioSampleFormat.Float64 => 64,
            _ => 0,
        };
    }

    private static uint GetChannelMask(ChannelLayout layout)
    {
        return layout switch
        {
            ChannelLayout.Mono => SpeakerFrontCenter,
            ChannelLayout.Stereo => SpeakerFrontLeft | SpeakerFrontRight,
            ChannelLayout.Surround21 => SpeakerFrontLeft | SpeakerFrontRight | SpeakerLowFrequency,
            ChannelLayout.Quad => SpeakerFrontLeft | SpeakerFrontRight | SpeakerBackLeft | SpeakerBackRight,
            ChannelLayout.Surround50 => SpeakerFrontLeft | SpeakerFrontRight | SpeakerFrontCenter | SpeakerBackLeft | SpeakerBackRight,
            ChannelLayout.Surround51 => SpeakerFrontLeft | SpeakerFrontRight | SpeakerFrontCenter | SpeakerLowFrequency | SpeakerBackLeft | SpeakerBackRight,
            ChannelLayout.Surround71 => SpeakerFrontLeft | SpeakerFrontRight | SpeakerFrontCenter | SpeakerLowFrequency | SpeakerBackLeft | SpeakerBackRight | SpeakerSideLeft | SpeakerSideRight,
            _ => ChannelMaskUnspecified,
        };
    }
}
