// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Playback.Backends.Speed;

internal sealed class SpeedAudioFilter : IAudioFilter
{
    private const int Int24Minimum = -8388608;
    private const int Int24Maximum = 8388607;

    private readonly ResamplingSpeedAlgorithm _resampling = new();
    private readonly WsolaSpeedAlgorithm _wsola = new();

    private long _speed = BitConverter.DoubleToInt64Bits(1.0);
    private long _algorithm = (long)AudioSpeedAlgorithm.None;
    private bool _disposed;

    // Only the audio thread touches these, which is what lets them be reused rather than allocated for every block.
    private double _applied = 1.0;
    private AudioSpeedAlgorithm _appliedAlgorithm = AudioSpeedAlgorithm.None;
    private double[][] _planes = [];
    private double[][] _output = [];

    public string Name => "Speed";

    public AudioStreamInfo? OutputFormat => null;

    /// <summary>The algorithm the next block will be fitted to the rate with.</summary>
    internal AudioSpeedAlgorithm Algorithm => (AudioSpeedAlgorithm)Interlocked.Read(ref _algorithm);

    /// <summary>
    /// Chooses how the sound is fitted to the rate. A value this filter does not know is ignored rather than taken as a reason to
    /// stop playing.
    /// </summary>
    internal void SetAlgorithm(AudioSpeedAlgorithm algorithm)
    {
        if (Volatile.Read(ref _disposed) || !Enum.IsDefined(algorithm))
        {
            return;
        }

        Interlocked.Exchange(ref _algorithm, (long)algorithm);
    }

    /// <summary>
    /// Sets how fast the sound plays. Anything this filter cannot honour is taken as no change at all rather than as a reason to
    /// stop playing.
    /// </summary>
    internal void SetSpeed(double speed)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        double value = double.IsFinite(speed) && speed > 0 ? speed : 1.0;
        Interlocked.Exchange(ref _speed, BitConverter.DoubleToInt64Bits(value));
    }

    public OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);

        // A new stream is a new sound: what was left over from the last one would only be a discontinuity in it. Both algorithms
        // are reset, because which one is asked for may change while nothing is playing.
        ResetAlgorithms(input.SampleRate);

        return Volatile.Read(ref _disposed)
            ? OperationResult.Failure("The filter has already been disposed.")
            : OperationResult.Success();
    }

    public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
    {
        ArgumentNullException.ThrowIfNull(input);
        output = input;

        if (Volatile.Read(ref _disposed))
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        double speed = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speed));
        AudioSpeedAlgorithm algorithm = Algorithm;
        if (speed != _applied || algorithm != _appliedAlgorithm)
        {
            // Neither what was left over from the last block nor what a different algorithm held makes sense at a new rate.
            _applied = speed;
            _appliedAlgorithm = algorithm;
            ResetAlgorithms(input.SampleRate);
        }

        if (speed == 1.0)
        {
            return OperationResult.Success();
        }

        if (input.Buffer is not IPlanarAudioBuffer planar)
        {
            return OperationResult.Failure("The audio buffer does not expose its planes.");
        }

        if (!TryGetBytesPerSample(input.SampleFormat, out int bytesPerSample))
        {
            return OperationResult.Failure($"The sample format '{input.SampleFormat}' has no sample size.");
        }

        int planeCount = planar.PlaneCount;
        if (planeCount <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no planes.");
        }

        // A single plane of a multi-channel block holds its channels interleaved, so a frame is several values there and one value
        // in a plane of its own.
        int valuesPerFrame = planeCount == 1 && input.ChannelCount > 1 ? input.ChannelCount : 1;
        int valuesPerPlane = planar.GetPlaneByteCount(0) / bytesPerSample;
        int sourceFrames = valuesPerPlane / valuesPerFrame;
        if (sourceFrames <= 0)
        {
            return OperationResult.Success();
        }

        // Reading past a plane is a fault in the block rather than something to average over: checked before anything is written.
        int sourceValues = sourceFrames * valuesPerFrame;
        for (int plane = 0; plane < planeCount; plane++)
        {
            if (planar.GetPlaneByteCount(plane) < sourceValues * bytesPerSample)
            {
                return OperationResult.Failure($"Plane {plane} of the audio buffer is shorter than the block it belongs to.");
            }
        }

        EnsurePlanes(ref _planes, planeCount, sourceValues);
        for (int plane = 0; plane < planeCount; plane++)
        {
            nint sourcePointer = planar.GetPlanePointer(plane);
            if (sourcePointer == 0)
            {
                return OperationResult.Failure($"Plane {plane} of the audio buffer has no memory.");
            }

            ReadPlane(sourcePointer, input.SampleFormat, _planes[plane], sourceValues);
        }

        // How much the block carries is a question about its media rather than about how it is fitted to the rate: the same span
        // goes in and comes out, whatever happens in between.
        int targetFrames = Math.Max(1, (int)Math.Round(sourceFrames / speed, MidpointRounding.AwayFromZero));
        EnsurePlanes(ref _output, planeCount, targetFrames * valuesPerFrame);

        // Two ways of fitting a block to the rate: the sound may simply follow it, which takes no algorithm of its own, or it may
        // be re-cut so that it keeps its pitch.
        ISpeedAlgorithm selected = algorithm == AudioSpeedAlgorithm.WSOLA ? _wsola : _resampling;
        int produced = selected.Apply(_planes, valuesPerFrame, sourceFrames, speed, _output, targetFrames);
        int producedValues = produced * valuesPerFrame;

        ResampledBuffer resampled = new(planeCount, producedValues * bytesPerSample);
        for (int plane = 0; plane < planeCount && produced > 0; plane++)
        {
            WritePlane(resampled.GetPlanePointer(plane), input.SampleFormat, _output[plane], producedValues);
        }

        output = new RawAudioBuffer
        {
            Timestamp = input.Timestamp,
            SampleRate = input.SampleRate,
            ChannelCount = input.ChannelCount,
            ChannelLayout = input.ChannelLayout,
            SampleFormat = input.SampleFormat,
            SampleCount = produced,
            Buffer = resampled,
        };

        return OperationResult.Success();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
    }

    /// <summary>Lets go of everything both algorithms hold, so the next block is fitted as if it were the first.</summary>
    private void ResetAlgorithms(int sampleRate)
    {
        _resampling.Reset(sampleRate);
        _wsola.Reset(sampleRate);
    }

    /// <summary>
    /// Room for one block's planes, each its own array so an algorithm can work on any plane without the others. The arrays are
    /// kept and only grown, because the audio thread sees these blocks for as long as the stream lasts.
    /// </summary>
    private static void EnsurePlanes(ref double[][] planes, int planeCount, int valuesPerPlane)
    {
        if (planes.Length != planeCount)
        {
            planes = new double[planeCount][];
        }

        for (int plane = 0; plane < planeCount; plane++)
        {
            if (planes[plane] is null || planes[plane].Length < valuesPerPlane)
            {
                planes[plane] = new double[valuesPerPlane];
            }
        }
    }

    /// <summary>
    /// How wide one stored value is, or false when the format describes no samples. A plane of an interleaved block holds one
    /// value per channel per frame, so counting values is the same arithmetic either way.
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

    /// <summary>Reads whole-sample values as numbers from -1 to 1, so every format is resampled by the same arithmetic.</summary>
    private static unsafe void ReadPlane(nint pointer, AudioSampleFormat format, double[] target, int valueCount)
    {
        byte* start = (byte*)pointer;

        switch (format)
        {
            case AudioSampleFormat.UInt8:
                {
                    for (int index = 0; index < valueCount; index++)
                    {
                        target[index] = (start[index] - 128) / 128.0;
                    }

                    break;
                }

            case AudioSampleFormat.Int16:
                {
                    short* values = (short*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        target[index] = values[index] / 32768.0;
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

                        target[index] = sample / 8388608.0;
                    }

                    break;
                }

            case AudioSampleFormat.Int32:
                {
                    int* values = (int*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        target[index] = values[index] / 2147483648.0;
                    }

                    break;
                }

            case AudioSampleFormat.Float32:
                {
                    float* values = (float*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        target[index] = values[index];
                    }

                    break;
                }

            case AudioSampleFormat.Float64:
                {
                    double* values = (double*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        target[index] = values[index];
                    }

                    break;
                }
        }
    }

    /// <summary>Writes numbers from -1 to 1 back as whole samples of the block's own format.</summary>
    private static unsafe void WritePlane(nint pointer, AudioSampleFormat format, double[] source, int valueCount)
    {
        byte* start = (byte*)pointer;

        switch (format)
        {
            case AudioSampleFormat.UInt8:
                {
                    for (int index = 0; index < valueCount; index++)
                    {
                        start[index] = (byte)Math.Clamp(Math.Round(128 + (source[index] * 128)), 0, 255);
                    }

                    break;
                }

            case AudioSampleFormat.Int16:
                {
                    short* values = (short*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (short)Math.Clamp(Math.Round(source[index] * 32768), short.MinValue, short.MaxValue);
                    }

                    break;
                }

            case AudioSampleFormat.Int24:
                {
                    for (int index = 0; index < valueCount; index++)
                    {
                        int sample = (int)Math.Clamp(Math.Round(source[index] * 8388608), Int24Minimum, Int24Maximum);
                        byte* value = start + (index * 3);
                        value[0] = (byte)sample;
                        value[1] = (byte)(sample >> 8);
                        value[2] = (byte)(sample >> 16);
                    }

                    break;
                }

            case AudioSampleFormat.Int32:
                {
                    int* values = (int*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (int)Math.Clamp(Math.Round(source[index] * 2147483648.0), int.MinValue, int.MaxValue);
                    }

                    break;
                }

            case AudioSampleFormat.Float32:
                {
                    float* values = (float*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = (float)source[index];
                    }

                    break;
                }

            case AudioSampleFormat.Float64:
                {
                    double* values = (double*)pointer;
                    for (int index = 0; index < valueCount; index++)
                    {
                        values[index] = source[index];
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// The resampled samples, in the same planes the block came in: one per channel for planar audio, one interleaved plane for
    /// the layout a device consumes.
    /// </summary>
    private sealed class ResampledBuffer : IPlanarAudioBuffer
    {
        private readonly byte[] _bytes;
        private readonly int _planeByteCount;
        private readonly GCHandle _handle;
        private bool _disposed;

        internal ResampledBuffer(int planeCount, int planeByteCount)
        {
            _planeByteCount = planeByteCount;
            _bytes = new byte[planeCount * planeByteCount];
            _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
        }

        public int PlaneCount => _planeByteCount > 0 ? _bytes.Length / _planeByteCount : 0;

        public long Length => _bytes.Length;

        public nint GetPlanePointer(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return plane >= 0 && plane < PlaneCount ? _handle.AddrOfPinnedObject() + (plane * _planeByteCount) : 0;
        }

        public int GetPlaneByteCount(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return plane >= 0 && plane < PlaneCount ? _planeByteCount : 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }
}
