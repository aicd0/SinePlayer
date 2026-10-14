// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// Turns a decoded block into the interleaved layout an audio device consumes. Interleaved input is
/// copied as is; planar input has its channels woven together, which is the only conversion the
/// renderer performs itself — rate and speaker mixing are left to the audio engine.
/// </summary>
internal static class AudioInterleaver
{
    internal static bool TryInterleave(RawAudioBuffer buffer, byte[] destination, out int byteCount, out string error)
    {
        byteCount = 0;
        error = string.Empty;

        if (buffer.Buffer is not IPlanarAudioBuffer planar)
        {
            error = "The audio buffer does not expose its planes.";
            return false;
        }

        int bytesPerSample = WasapiAudioFormat.GetBytesPerSample(buffer.SampleFormat);
        if (bytesPerSample <= 0)
        {
            error = $"The sample format '{buffer.SampleFormat}' has no sample size.";
            return false;
        }

        int channels = buffer.ChannelCount;
        int samples = buffer.SampleCount;
        if (channels <= 0 || samples <= 0)
        {
            error = "The audio buffer holds no samples.";
            return false;
        }

        int required = bytesPerSample * channels * samples;
        if (destination.Length < required)
        {
            error = $"The destination holds {destination.Length} byte(s) but {required} are needed.";
            return false;
        }

        if (planar.PlaneCount == 1)
        {
            nint source = planar.GetPlanePointer(0);
            int available = planar.GetPlaneByteCount(0);
            if (source == 0 || available < required)
            {
                error = $"The source holds {available} byte(s) but {required} are needed.";
                return false;
            }

            Marshal.Copy(source, destination, 0, required);
            byteCount = required;
            return true;
        }

        if (planar.PlaneCount < channels)
        {
            error = $"The source has {planar.PlaneCount} plane(s) for {channels} channel(s).";
            return false;
        }

        Span<byte> output = destination.AsSpan(0, required);
        for (int channel = 0; channel < channels; channel++)
        {
            nint plane = planar.GetPlanePointer(channel);
            if (plane == 0 || planar.GetPlaneByteCount(channel) < bytesPerSample * samples)
            {
                error = $"Plane {channel} does not hold {samples} sample(s).";
                return false;
            }

            for (int sample = 0; sample < samples; sample++)
            {
                int sourceOffset = sample * bytesPerSample;
                int destinationOffset = ((sample * channels) + channel) * bytesPerSample;
                for (int index = 0; index < bytesPerSample; index++)
                {
                    output[destinationOffset + index] = Marshal.ReadByte(plane + sourceOffset + index);
                }
            }
        }

        byteCount = required;
        return true;
    }
}
