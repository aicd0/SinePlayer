// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Backends.Gain;

/// <summary>
/// Multiplies every sample by a level. It is the filter that proves the audio chain, and the one that makes the
/// sound's level a property of the pipeline rather than of the device playing it: a renderer with no device still
/// gets a working level, and nothing has to write the system mixer's volume on the user's behalf.
/// <para>
/// It scales in place and allocates nothing, reads the sample format from each block rather than from the stream
/// (a decoder reports what each frame actually is, which need not be what the container declared), and leaves a
/// block at full level untouched. Muting is a level of zero rather than a skipped block, so the block keeps its
/// length and timestamp and the clock sees the same stream either way. Silence is the format's own zero: 0 for
/// signed and floating samples, 128 for unsigned bytes.
/// </para>
/// <para>
/// The level is one value written whole and read once per block, which is what lets it be set from any thread
/// while the audio thread is transforming — and what makes a block be scaled by one level or another, never by a
/// mixture of the two.
/// </para>
/// </summary>
public sealed class GainAudioFilter : IAudioFilter
{
    private const int Int24Minimum = -8388608;
    private const int Int24Maximum = 8388607;

    private long _gain = BitConverter.DoubleToInt64Bits(1.0);
    private bool _disposed;

    public string Name => "gain";

    public void SetLevel(double volume, bool muted)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        if (!double.IsFinite(volume))
        {
            volume = 1.0;
        }

        double gain = muted ? 0.0 : Math.Clamp(volume, 0.0, 1.0);
        Interlocked.Exchange(ref _gain, BitConverter.DoubleToInt64Bits(gain));
    }

    public PlaybackOperationResult Configure(AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return Volatile.Read(ref _disposed)
            ? PlaybackOperationResult.Failure("The filter has already been disposed.")
            : PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
    {
        ArgumentNullException.ThrowIfNull(input);
        output = input;

        if (Volatile.Read(ref _disposed))
        {
            return PlaybackOperationResult.Failure("The filter has already been disposed.");
        }

        double gain = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _gain));
        if (gain == 1.0)
        {
            return PlaybackOperationResult.Success;
        }

        if (input.Buffer is not IPlanarAudioBuffer planar)
        {
            return PlaybackOperationResult.Failure("The audio buffer does not expose its planes.");
        }

        if (!TryGetBytesPerSample(input.SampleFormat, out int bytesPerSample))
        {
            return PlaybackOperationResult.Failure($"The sample format '{input.SampleFormat}' has no sample size.");
        }

        int planeCount = planar.PlaneCount;
        if (planeCount <= 0)
        {
            return PlaybackOperationResult.Failure("The audio buffer holds no planes.");
        }

        // Checked before anything is written: a filter that cannot finish leaves the block as it found it.
        for (int plane = 0; plane < planeCount; plane++)
        {
            if (planar.GetPlaneByteCount(plane) > 0 && planar.GetPlanePointer(plane) == 0)
            {
                return PlaybackOperationResult.Failure($"Plane {plane} of the audio buffer has no memory.");
            }
        }

        for (int plane = 0; plane < planeCount; plane++)
        {
            int byteCount = planar.GetPlaneByteCount(plane);
            if (byteCount <= 0)
            {
                continue;
            }

            Scale(planar.GetPlanePointer(plane), byteCount / bytesPerSample, input.SampleFormat, gain);
        }

        return PlaybackOperationResult.Success;
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
    }

    /// <summary>
    /// How wide one stored value is, or false when the format describes no samples. A plane of an interleaved
    /// block holds one value per channel per sample, so counting values is the same arithmetic either way.
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
