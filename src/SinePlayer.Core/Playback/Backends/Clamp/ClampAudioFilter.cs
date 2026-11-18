// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Backends.Clamp;

internal sealed partial class ClampAudioFilter : IAudioFilter
{
    private const string TAG = nameof(ClampAudioFilter);

    private AudioStreamInfo? _outputFormat;
    private bool _disposed;
    private bool _reportedNonFinite;
    private long _clippedSamples;

    public string Name => "Clamp";

    public AudioStreamInfo? OutputFormat => _outputFormat;

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    public OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target)
    {
        _outputFormat = null;
        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        _outputFormat = input;
        return OperationResult.Success();
    }

    public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
    {
        output = input;

        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        if (input.SampleFormat != AudioSampleFormat.Float32)
        {
            return OperationResult.Failure($"The block holds '{input.SampleFormat}' where the chain works in '{AudioSampleFormat.Float32}'.");
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

        bool interleaved = planar.PlaneCount == 1;
        int planeCount = interleaved ? 1 : channels;
        for (int plane = 0; plane < planeCount; plane++)
        {
            int valueCount = sampleCount * (interleaved ? channels : 1);
            nint pointer = planar.GetPlanePointer(plane);
            if (pointer == 0 || planar.GetPlaneByteCount(plane) < valueCount * sizeof(float))
            {
                return OperationResult.Failure($"Plane {plane} does not hold {sampleCount} sample(s).");
            }

            Clamp(pointer, valueCount);
        }

        return OperationResult.Success();
    }

    public void Dispose()
    {
        _disposed = true;
        _outputFormat = null;
    }

    private void Clamp(nint pointer, int valueCount)
    {
        int clipped = 0;
        bool silenced = false;
        unsafe
        {
            float* values = (float*)pointer;
            for (int index = 0; index < valueCount; index++)
            {
                float value = values[index];
                if (!float.IsFinite(value))
                {
                    values[index] = 0.0f;
                    silenced = true;
                    continue;
                }

                if (value > 1.0f || value < -1.0f)
                {
                    values[index] = value > 0.0f ? 1.0f : -1.0f;
                    clipped++;
                }
            }
        }

        if (clipped > 0)
        {
            Interlocked.Add(ref _clippedSamples, clipped);
        }

        if (silenced)
        {
            ReportSilenced();
        }
    }

    private void ReportSilenced()
    {
        if (_reportedNonFinite)
        {
            return;
        }

        _reportedNonFinite = true;
        PlaybackLogger.W(TAG, "A sample that is not a number reached the end of the audio chain and was silenced.");
    }
}
