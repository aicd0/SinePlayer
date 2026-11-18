// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Backends.Conversion;

internal sealed partial class ConversionAudioFilter : IAudioFilter
{
    private const int Int24SignBit = 0x800000;

    private AudioStreamInfo? _outputFormat;
    private bool _disposed;

    public string Name => "Conversion";

    public AudioStreamInfo? OutputFormat => _outputFormat;

    public OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);

        _outputFormat = null;
        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        _outputFormat = input.WithSampleFormat(AudioSampleFormat.Float32);
        return OperationResult.Success();
    }

    public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
    {
        ArgumentNullException.ThrowIfNull(input);

        output = input;

        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        AudioSampleFormat format = input.SampleFormat;
        if (format == AudioSampleFormat.Float32)
        {
            return OperationResult.Success();
        }

        if (input.Buffer is not IPlanarAudioBuffer planar)
        {
            return OperationResult.Failure("The audio buffer does not expose its planes.");
        }

        int channels = input.ChannelCount;
        int sampleCount = input.SampleCount;
        if (channels <= 0 || sampleCount <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no samples.");
        }

        int bytesPerSample = format switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float64 => 8,
            _ => 0,
        };
        if (bytesPerSample == 0)
        {
            return OperationResult.Failure($"The sample format '{format}' cannot be converted.");
        }

        bool interleaved = planar.PlaneCount == 1;
        if (!interleaved && planar.PlaneCount < channels)
        {
            return OperationResult.Failure($"The source has {planar.PlaneCount} plane(s) for {channels} channel(s).");
        }

        int planeCount = interleaved ? 1 : channels;
        nint[] planes = new nint[planeCount];
        for (int plane = 0; plane < planeCount; plane++)
        {
            int needed = bytesPerSample * (interleaved ? channels : 1) * sampleCount;
            nint pointer = planar.GetPlanePointer(plane);
            if (pointer == 0 || planar.GetPlaneByteCount(plane) < needed)
            {
                return OperationResult.Failure($"Plane {plane} does not hold {sampleCount} sample(s).");
            }

            planes[plane] = pointer;
        }

        int stride = interleaved ? channels * bytesPerSample : bytesPerSample;
        float[] samples = new float[sampleCount * channels];
        unsafe
        {
            fixed (float* destination = samples)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    // An interleaved block holds the channels in one plane, a frame's worth apart.
                    byte* source = interleaved
                        ? (byte*)planes[0] + ((long)channel * bytesPerSample)
                        : (byte*)planes[channel];
                    Read(source, stride, format, sampleCount, destination + ((long)channel * sampleCount));
                }
            }
        }

        output = new RawAudioBuffer
        {
            Timestamp = input.Timestamp,
            SampleRate = input.SampleRate,
            ChannelCount = channels,
            ChannelLayout = input.ChannelLayout,
            SampleFormat = AudioSampleFormat.Float32,
            SampleCount = sampleCount,
            Buffer = new FloatBuffer(samples, channels),
        };
        return OperationResult.Success();
    }

    public void Dispose()
    {
        _disposed = true;
        _outputFormat = null;
    }

    /// <summary>One channel's samples as levels, read from where the block holds them.</summary>
    private static unsafe void Read(byte* source, int stride, AudioSampleFormat format, int sampleCount, float* destination)
    {
        switch (format)
        {
            case AudioSampleFormat.UInt8:
                for (int index = 0; index < sampleCount; index++)
                {
                    destination[index] = (source[(long)index * stride] - 128) / 128.0f;
                }

                break;

            case AudioSampleFormat.Int16:
                for (int index = 0; index < sampleCount; index++)
                {
                    destination[index] = *(short*)(source + ((long)index * stride)) / 32768.0f;
                }

                break;

            case AudioSampleFormat.Int24:
                for (int index = 0; index < sampleCount; index++)
                {
                    byte* address = source + ((long)index * stride);
                    int value = address[0] | (address[1] << 8) | (address[2] << 16);
                    if ((value & Int24SignBit) != 0)
                    {
                        value |= ~0xFFFFFF;
                    }

                    destination[index] = value / 8388608.0f;
                }

                break;

            case AudioSampleFormat.Int32:
                for (int index = 0; index < sampleCount; index++)
                {
                    destination[index] = (float)(*(int*)(source + ((long)index * stride)) / 2147483648.0);
                }

                break;

            case AudioSampleFormat.Float64:
                for (int index = 0; index < sampleCount; index++)
                {
                    destination[index] = (float)*(double*)(source + ((long)index * stride));
                }

                break;
        }
    }

    /// <summary>A block of levels, one plane per channel, in the shape the rest of the chain reads.</summary>
    private sealed partial class FloatBuffer : IPlanarAudioBuffer
    {
        private readonly float[] _samples;
        private readonly int _planeCount;
        private readonly int _planeByteCount;
        private readonly GCHandle _handle;
        private bool _disposed;

        internal FloatBuffer(float[] samples, int channelCount)
        {
            _samples = samples;
            _planeCount = channelCount;
            _planeByteCount = samples.Length / channelCount * sizeof(float);
            _handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
        }

        public int PlaneCount => _planeCount;

        public long Length => _samples.Length * sizeof(float);

        public nint GetPlanePointer(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle.AddrOfPinnedObject() + (nint)((long)CheckPlane(plane) * _planeByteCount);
        }

        public int GetPlaneByteCount(int plane)
        {
            CheckPlane(plane);
            return _planeByteCount;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handle.Free();
        }

        private int CheckPlane(int plane)
        {
            return plane >= 0 && plane < _planeCount
                ? plane
                : throw new ArgumentOutOfRangeException(nameof(plane), plane, $"A converted block has {_planeCount} plane(s).");
        }
    }
}
