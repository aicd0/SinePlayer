// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Backends.Downmix;

/// <summary>
/// Turns a surround block into two channels. It is a filter rather than part of a renderer because folding a mix
/// down is arithmetic on samples — a coefficient table and a per-format read and write are the whole of it — and
/// none of that needs a device to be understood or to be tested.
/// <para>
/// A host adds it to a pipeline the way it adds any other filter, and the format the chain is pointed at is what
/// tells it whether there is anything to do: an endpoint with two speakers makes a five-channel mix worth folding,
/// and a device that can play all six leaves this filter costing a reference. Nothing here knows that WASAPI,
/// or any other renderer, exists.
/// </para>
/// <para>
/// The coefficients are the usual ones: front channels at unity, the centre and the surrounds at -3 dB each, and
/// the low-frequency channel dropped rather than sent to speakers that cannot reproduce it. Unity front channels
/// leave no headroom for what is added to them, so the result is clamped to full scale rather than allowed to wrap.
/// </para>
/// </summary>
public sealed partial class DownmixAudioFilter : IAudioFilter
{
    private const int TargetChannelCount = 2;

    private const float HalfPower = 0.70710678f;

    private float[]? _left;
    private float[]? _right;
    private AudioStreamInfo? _outputFormat;
    private bool _disposed;

    public string Name => "Downmix";

    public AudioStreamInfo? OutputFormat => _outputFormat;

    /// <summary>
    /// Prepares the fold for a stream and for what the next stage can take. The decision is here rather than in the
    /// caller: a stream that already fits the target is accepted and then left alone, a layout whose speaker
    /// positions are unknown is refused because dropping the channels a mask does not account for would silence
    /// part of the mix rather than place it, and a target that is neither the stream's own count nor two channels
    /// is refused because two is the only fold this filter knows how to do.
    /// </summary>
    public OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        _left = null;
        _right = null;
        _outputFormat = null;

        if (input.ChannelCount <= target.ChannelCount)
        {
            return OperationResult.Success();
        }

        if (target.ChannelCount != TargetChannelCount)
        {
            return OperationResult.Failure($"A target of {target.ChannelCount} channels is not one this filter can fold to; it produces {TargetChannelCount}.");
        }

        if (!TryCreateStereo(input.ChannelLayout, input.ChannelCount, out float[] left, out float[] right))
        {
            return OperationResult.Failure($"The {input.ChannelCount} channel layout '{input.ChannelLayout}' has no fold down to {TargetChannelCount} channels.");
        }

        _left = left;
        _right = right;
        _outputFormat = input.WithLayout(TargetChannelCount, ChannelLayout.Stereo);
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

        float[]? left = _left;
        float[]? right = _right;
        if (left is null || right is null)
        {
            return OperationResult.Success();
        }

        if (input.Buffer is not IPlanarAudioBuffer planar)
        {
            return OperationResult.Failure("The audio buffer does not expose its planes.");
        }

        AudioSampleFormat format = input.SampleFormat;
        if (!IsSupported(format))
        {
            return OperationResult.Failure($"The sample format '{format}' cannot be folded.");
        }

        int channels = input.ChannelCount;
        int samples = input.SampleCount;
        if (channels <= 0 || samples <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no samples.");
        }

        if (left.Length < channels)
        {
            return OperationResult.Failure($"The fold takes {left.Length} channel(s) but the buffer holds {channels}.");
        }

        bool interleaved = planar.PlaneCount == 1;
        if (!interleaved && planar.PlaneCount < channels)
        {
            return OperationResult.Failure($"The source has {planar.PlaneCount} plane(s) for {channels} channel(s).");
        }

        // The sample width is read from the buffer rather than from a table: the block says what it holds, and a
        // format whose width is derived from its own data cannot disagree with the arithmetic that follows.
        int firstPlaneBytes = planar.GetPlaneByteCount(0);
        int bytesPerSample = firstPlaneBytes / (interleaved ? samples * channels : samples);
        if (bytesPerSample <= 0)
        {
            return OperationResult.Failure($"Plane 0 holds {firstPlaneBytes} byte(s), which is no whole sample.");
        }

        int planeCount = interleaved ? 1 : channels;
        nint[] planes = new nint[planeCount];
        for (int plane = 0; plane < planeCount; plane++)
        {
            int needed = bytesPerSample * (interleaved ? channels : 1) * samples;
            nint pointer = planar.GetPlanePointer(plane);
            if (pointer == 0 || planar.GetPlaneByteCount(plane) < needed)
            {
                return OperationResult.Failure($"Plane {plane} does not hold {samples} sample(s).");
            }

            planes[plane] = pointer;
        }

        byte[] folded = new byte[bytesPerSample * TargetChannelCount * samples];
        Fold(planes, interleaved, format, bytesPerSample, channels, samples, left, right, folded);

        output = new RawAudioBuffer
        {
            Timestamp = input.Timestamp,
            SampleRate = input.SampleRate,
            ChannelCount = TargetChannelCount,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = format,
            SampleCount = samples,
            Buffer = new FoldedBuffer(folded),
        };
        return OperationResult.Success();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
    }

    private static unsafe void Fold(nint[] planes, bool interleaved, AudioSampleFormat format, int bytesPerSample, int channels, int samples, float[] left, float[] right, byte[] destination)
    {
        int frameBytes = bytesPerSample * TargetChannelCount;
        fixed (byte* target = destination)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                double sumLeft = 0.0;
                double sumRight = 0.0;
                for (int channel = 0; channel < channels; channel++)
                {
                    byte* address = interleaved
                        ? (byte*)planes[0] + ((long)((sample * channels) + channel) * bytesPerSample)
                        : (byte*)planes[channel] + ((long)sample * bytesPerSample);
                    double value = ReadSample(address, format);
                    sumLeft += value * left[channel];
                    sumRight += value * right[channel];
                }

                byte* destinationFrame = target + ((long)sample * frameBytes);
                WriteSample(destinationFrame, format, sumLeft);
                WriteSample(destinationFrame + bytesPerSample, format, sumRight);
            }
        }
    }

    /// <summary>
    /// The fold for a layout, or false when this is not a layout whose speaker positions are known here. A channel
    /// count that does not match the layout is refused for the same reason as an unknown layout.
    /// </summary>
    private static bool TryCreateStereo(ChannelLayout layout, int channelCount, out float[] left, out float[] right)
    {
        left = [];
        right = [];

        int expected = layout switch
        {
            ChannelLayout.Stereo => 2,
            ChannelLayout.Surround21 => 3,
            ChannelLayout.Quad => 4,
            ChannelLayout.Surround50 => 5,
            ChannelLayout.Surround51 => 6,
            ChannelLayout.Surround71 => 8,
            _ => 0,
        };

        if (expected == 0 || channelCount != expected)
        {
            return false;
        }

        // Indices follow the layout's own channel order, which is the order the decoder emits and the order the
        // channel mask asserts: front pair, centre, low frequency, then the surrounds.
        left = new float[channelCount];
        right = new float[channelCount];
        left[0] = 1.0f;
        right[1] = 1.0f;

        switch (layout)
        {
            case ChannelLayout.Quad:
                left[2] = HalfPower;
                right[3] = HalfPower;
                break;

            case ChannelLayout.Surround50:
                left[2] = HalfPower;
                right[2] = HalfPower;
                left[3] = HalfPower;
                right[4] = HalfPower;
                break;

            case ChannelLayout.Surround51:
                left[2] = HalfPower;
                right[2] = HalfPower;
                left[4] = HalfPower;
                right[5] = HalfPower;
                break;

            case ChannelLayout.Surround71:
                left[2] = HalfPower;
                right[2] = HalfPower;
                left[4] = HalfPower;
                right[5] = HalfPower;
                left[6] = HalfPower;
                right[7] = HalfPower;
                break;
        }

        return true;
    }

    private static bool IsSupported(AudioSampleFormat format)
    {
        return format is AudioSampleFormat.UInt8
            or AudioSampleFormat.Int16
            or AudioSampleFormat.Int24
            or AudioSampleFormat.Int32
            or AudioSampleFormat.Float32
            or AudioSampleFormat.Float64;
    }

    /// <summary>A stored sample as a level, so formats mix with the same arithmetic whatever they store.</summary>
    private static unsafe double ReadSample(byte* address, AudioSampleFormat format)
    {
        return format switch
        {
            AudioSampleFormat.UInt8 => (*address - 128) / 128.0,
            AudioSampleFormat.Int16 => *(short*)address / 32768.0,
            AudioSampleFormat.Int24 => ReadInt24(address) / 8388608.0,
            AudioSampleFormat.Int32 => *(int*)address / 2147483648.0,
            AudioSampleFormat.Float32 => *(float*)address,
            AudioSampleFormat.Float64 => *(double*)address,
            _ => 0.0,
        };
    }

    private static unsafe void WriteSample(byte* address, AudioSampleFormat format, double value)
    {
        double clamped = Math.Clamp(value, -1.0, 1.0);
        switch (format)
        {
            case AudioSampleFormat.UInt8:
                *address = (byte)Math.Clamp(Math.Round((clamped * 128.0) + 128.0), 0.0, 255.0);
                break;

            case AudioSampleFormat.Int16:
                *(short*)address = (short)Math.Clamp(Math.Round(clamped * 32767.0), short.MinValue, short.MaxValue);
                break;

            case AudioSampleFormat.Int24:
                int value24 = (int)Math.Clamp(Math.Round(clamped * 8388607.0), -8388608.0, 8388607.0);
                address[0] = (byte)value24;
                address[1] = (byte)(value24 >> 8);
                address[2] = (byte)(value24 >> 16);
                break;

            case AudioSampleFormat.Int32:
                *(int*)address = (int)Math.Clamp(Math.Round(clamped * 2147483647.0), int.MinValue, int.MaxValue);
                break;

            case AudioSampleFormat.Float32:
                *(float*)address = (float)clamped;
                break;

            case AudioSampleFormat.Float64:
                *(double*)address = clamped;
                break;
        }
    }

    private static unsafe int ReadInt24(byte* address)
    {
        int value = address[0] | (address[1] << 8) | (address[2] << 16);
        return (value & 0x800000) != 0 ? value | ~0xFFFFFF : value;
    }

    /// <summary>The folded block, interleaved so the renderer has nothing left to do but copy it to the device.</summary>
    private sealed class FoldedBuffer : IPlanarAudioBuffer
    {
        private readonly byte[] _bytes;
        private readonly GCHandle _handle;
        private bool _disposed;

        internal FoldedBuffer(byte[] bytes)
        {
            _bytes = bytes;
            _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        }

        public int PlaneCount => 1;

        public long Length => _bytes.Length;

        public nint GetPlanePointer(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return plane == 0 ? _handle.AddrOfPinnedObject() : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A folded block has one plane.");
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane == 0 ? _bytes.Length : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A folded block has one plane.");
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
    }
}
