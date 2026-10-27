// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// Turns a decoded block into the interleaved layout an audio device consumes. Interleaved input is
/// copied as is; planar input has its channels woven together, which is the only conversion the
/// renderer performs itself — rate and speaker mixing are left to the audio engine.
/// </summary>
internal static class AudioInterleaver
{
    internal static OperationResult<int> TryInterleave(RawAudioBuffer buffer, byte[] destination)
    {
        OperationResultCreator<int> result = new();

        if (buffer.Buffer is not IPlanarAudioBuffer planar)
        {
            return result.Failure("The audio buffer does not expose its planes.");
        }

        int bytesPerSample = WasapiAudioFormat.GetBytesPerSample(buffer.SampleFormat);
        if (bytesPerSample <= 0)
        {
            return result.Failure($"The sample format '{buffer.SampleFormat}' has no sample size.");
        }

        int channels = buffer.ChannelCount;
        int samples = buffer.SampleCount;
        if (channels <= 0 || samples <= 0)
        {
            return result.Failure("The audio buffer holds no samples.");
        }

        int required = bytesPerSample * channels * samples;
        if (destination.Length < required)
        {
            return result.Failure($"The destination holds {destination.Length} byte(s) but {required} are needed.");
        }

        if (planar.PlaneCount == 1)
        {
            nint source = planar.GetPlanePointer(0);
            int available = planar.GetPlaneByteCount(0);
            if (source == 0 || available < required)
            {
                return result.Failure($"The source holds {available} byte(s) but {required} are needed.");
            }

            Marshal.Copy(source, destination, 0, required);
            return result.Success(required);
        }

        if (planar.PlaneCount < channels)
        {
            return result.Failure($"The source has {planar.PlaneCount} plane(s) for {channels} channel(s).");
        }

        Span<byte> output = destination.AsSpan(0, required);
        for (int channel = 0; channel < channels; channel++)
        {
            nint plane = planar.GetPlanePointer(channel);
            if (plane == 0 || planar.GetPlaneByteCount(channel) < bytesPerSample * samples)
            {
                return result.Failure($"Plane {channel} does not hold {samples} sample(s).");
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

        return result.Success(required);
    }
}
