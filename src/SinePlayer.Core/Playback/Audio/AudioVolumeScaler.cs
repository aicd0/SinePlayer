// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Audio;

internal static class AudioVolumeScaler
{
    private const int Int24Minimum = -8388608;
    private const int Int24Maximum = 8388607;

    internal static OperationResult TryApply(IPlanarAudioBuffer planar, AudioSampleFormat format, double gain)
    {
        if (gain == 1.0)
        {
            return OperationResult.Success();
        }

        if (!TryGetBytesPerSample(format, out int bytesPerSample))
        {
            return OperationResult.Failure($"The sample format '{format}' has no sample size.");
        }

        int planeCount = planar.PlaneCount;
        if (planeCount <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no planes.");
        }

        // Checked before anything is written: a block that cannot be scaled is left as it was found.
        for (int plane = 0; plane < planeCount; plane++)
        {
            if (planar.GetPlaneByteCount(plane) > 0 && planar.GetPlanePointer(plane) == 0)
            {
                return OperationResult.Failure($"Plane {plane} of the audio buffer has no memory.");
            }
        }

        for (int plane = 0; plane < planeCount; plane++)
        {
            int byteCount = planar.GetPlaneByteCount(plane);
            if (byteCount <= 0)
            {
                continue;
            }

            Scale(planar.GetPlanePointer(plane), byteCount / bytesPerSample, format, gain);
        }

        return OperationResult.Success();
    }

    /// <summary>
    /// How wide one stored value is, or false when the format describes no samples. A plane of an interleaved block
    /// holds one value per channel per sample, so counting values is the same arithmetic either way.
    /// </summary>
    private static bool TryGetBytesPerSample(AudioSampleFormat format, out int bytesPerSample)
    {
        bytesPerSample = format switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
            AudioSampleFormat.Float64 => 8,
            _ => 0,
        };

        return bytesPerSample > 0;
    }

    private static unsafe void Scale(nint pointer, int valueCount, AudioSampleFormat format, double gain)
    {
        byte* start = (byte*)pointer;

        switch (format)
        {
            case AudioSampleFormat.UInt8:
                {
                    for (int index = 0; index < valueCount; index++)
                    {
                        start[index] = (byte)Math.Clamp(Rounded(128 + ((start[index] - 128) * gain)), 0, 255);
                    }

                    break;
                }

            case AudioSampleFormat.Int16:
                {
                    short* values = (short*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (short)Math.Clamp(Rounded(values[index] * gain), short.MinValue, short.MaxValue);
                    }

                    break;
                }

            case AudioSampleFormat.Int24:
                {
                    for (int index = 0; index < valueCount; index++)
                    {
                        byte* value = start + (index * 3);
                        int sample = value[0] | (value[1] << 8) | (value[2] << 16);
                        if ((sample & 0x800000) != 0)
                        {
                            sample |= ~0xFFFFFF;
                        }

                        int scaled = Math.Clamp(Rounded(sample * gain), Int24Minimum, Int24Maximum);
                        value[0] = (byte)scaled;
                        value[1] = (byte)(scaled >> 8);
                        value[2] = (byte)(scaled >> 16);
                    }

                    break;
                }

            case AudioSampleFormat.Int32:
                {
                    int* values = (int*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (int)Math.Clamp(Rounded(values[index] * gain), int.MinValue, int.MaxValue);
                    }

                    break;
                }

            case AudioSampleFormat.Float32:
                {
                    float* values = (float*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (float)(values[index] * gain);
                    }

                    break;
                }

            case AudioSampleFormat.Float64:
                {
                    double* values = (double*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] *= gain;
                    }

                    break;
                }
        }
    }

    private static int Rounded(double value)
    {
        return (int)Math.Round(value);
    }
}
