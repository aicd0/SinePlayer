// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

using FoldMatrix = SinePlayer.Core.Playback.Backends.Remix.RemixMixer.FoldMatrix;

namespace SinePlayer.Core.Playback.Backends.Remix;

public sealed partial class RemixAudioFilter : IAudioFilter
{
    private const int TargetChannelCount = 2;

    private readonly AtomicCell<MixState> _state = new(new MixState(ChannelLayout.Unknown, 0, 0, null, null, null, null));

    private AudioStreamInfo? _outputFormat;
    private bool _disposed;

    public string Name => "Remix";

    public AudioStreamInfo? OutputFormat => _outputFormat;

    /// <summary>Which set of levels a block of the configured stream is mixed with.</summary>
    public RemixLevelSet ActiveLevelSet
    {
        get
        {
            MixState state = _state.Read();
            return IsDownmix(state.ChannelCount, state.TargetChannelCount) ? RemixLevelSet.Downmix : RemixLevelSet.Remix;
        }
    }

    public OperationResult Configure(AudioStreamInfo input, AudioStreamInfo target)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        _outputFormat = null;
        MixState current = _state.Read();

        if (IsDownmix(input.ChannelCount, target.ChannelCount))
        {
            if (!RemixMixer.TryCreateDownmix(input.ChannelLayout, input.ChannelCount, target.ChannelCount, current.DownmixLevels, out _))
            {
                // Nothing is left behind that a later block could be mixed with: the chain has no filter to hand a
                // block to once the configuration is refused.
                _state.Write(_ => new MixState(input.ChannelLayout, input.ChannelCount, target.ChannelCount, current.RemixLevels, current.DownmixLevels, null, null));
                return OperationResult.Failure($"The {input.ChannelCount} channel layout '{input.ChannelLayout}' has no downmix to {target.ChannelCount} channel(s).");
            }

            _outputFormat = input.WithLayout(target.ChannelCount, target.ChannelCount == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo);
        }

        MixState next = new(input.ChannelLayout, input.ChannelCount, target.ChannelCount, current.RemixLevels, current.DownmixLevels, null, null);
        _state.Write(_ => BuildMix(next));
        return OperationResult.Success();
    }

    /// <summary>
    /// The levels of one set, as percentages of full scale keyed by group. A group that is absent keeps the default the
    /// mixer gives it; a value outside 0-200 is clamped. Nothing about the format changes, so the mix is rebuilt for the
    /// arrangement the filter was configured with and the next block uses it without the device being reopened.
    /// </summary>
    public void SetLevels(RemixLevelSet set, IReadOnlyDictionary<string, int> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        // Copied so the mix is built from levels that cannot change under the thread building it.
        IReadOnlyDictionary<string, int> snapshot = new Dictionary<string, int>(levels);
        _state.Write(state =>
        {
            MixState next = set == RemixLevelSet.Remix ? state with { RemixLevels = snapshot } : state with { DownmixLevels = snapshot };
            return BuildMix(next);
        });
    }

    public OperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output)
    {
        ArgumentNullException.ThrowIfNull(input);

        output = input;

        if (_disposed)
        {
            return OperationResult.Failure("The filter has already been disposed.");
        }

        MixState state = _state.Read();
        FoldMatrix? matrix = state.Matrix;
        float[]? gains = state.Gains;
        if (matrix is null && gains is null)
        {
            return OperationResult.Success();
        }

        if (input.Buffer is not IPlanarAudioBuffer planar)
        {
            return OperationResult.Failure("The audio buffer does not expose its planes.");
        }

        AudioSampleFormat format = input.SampleFormat;
        if (format != AudioSampleFormat.Float32)
        {
            return OperationResult.Failure($"The sample format '{format}' cannot be mixed.");
        }

        int channels = input.ChannelCount;
        int samples = input.SampleCount;
        if (channels <= 0 || samples <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no samples.");
        }

        // Both paths take one coefficient per source channel; only the width of the result differs.
        int expected = matrix?.Left.Length ?? gains!.Length;
        if (expected < channels)
        {
            return OperationResult.Failure($"The mix takes {expected} channel(s) but the buffer holds {channels}.");
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
            int needed = sizeof(float) * (interleaved ? channels : 1) * samples;
            nint pointer = planar.GetPlanePointer(plane);
            if (pointer == 0 || planar.GetPlaneByteCount(plane) < needed)
            {
                return OperationResult.Failure($"Plane {plane} does not hold {samples} sample(s).");
            }

            planes[plane] = pointer;
        }

        if (matrix is { } fold)
        {
            int outputChannels = fold.ChannelCount;
            byte[] mixed = new byte[sizeof(float) * outputChannels * samples];
            Fold(planes, interleaved, channels, samples, fold, mixed);

            output = new RawAudioBuffer
            {
                Timestamp = input.Timestamp,
                SampleRate = input.SampleRate,
                ChannelCount = outputChannels,
                ChannelLayout = outputChannels == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo,
                SampleFormat = format,
                SampleCount = samples,
                Buffer = new MixedBuffer(mixed),
            };
            return OperationResult.Success();
        }

        Scale(planes, interleaved, channels, samples, gains!);
        return OperationResult.Success();
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
    }

    private static MixState BuildMix(MixState state)
    {
        if (IsDownmix(state.ChannelCount, state.TargetChannelCount))
        {
            FoldMatrix? matrix = RemixMixer.TryCreateDownmix(state.Layout, state.ChannelCount, state.TargetChannelCount, state.DownmixLevels, out FoldMatrix built) ? built : null;
            return state with { Matrix = matrix, Gains = null };
        }

        float[]? gains = RemixMixer.TryCreateRemix(state.Layout, state.ChannelCount, state.RemixLevels, out float[] trimmed) ? trimmed : null;
        return state with { Matrix = null, Gains = gains };
    }

    /// <summary>Whether a stream of this width is folded down to the target rather than trimmed in place.</summary>
    private static bool IsDownmix(int channelCount, int targetChannelCount)
    {
        return channelCount > targetChannelCount && targetChannelCount <= TargetChannelCount;
    }

    private static unsafe void Fold(nint[] planes, bool interleaved, int channels, int samples, FoldMatrix matrix, byte[] destination)
    {
        int frameBytes = sizeof(float) * matrix.ChannelCount;
        fixed (byte* target = destination)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                double sumLeft = 0.0;
                double sumRight = 0.0;
                for (int channel = 0; channel < channels; channel++)
                {
                    float* address = interleaved
                        ? (float*)planes[0] + ((sample * channels) + channel)
                        : (float*)planes[channel] + sample;
                    double value = *address;
                    sumLeft += value * matrix.Left[channel];
                    if (matrix.ChannelCount > 1)
                    {
                        sumRight += value * matrix.Right[channel];
                    }
                }

                float* destinationFrame = (float*)(target + ((long)sample * frameBytes));
                *destinationFrame = (float)sumLeft;
                if (matrix.ChannelCount > 1)
                {
                    *(destinationFrame + 1) = (float)sumRight;
                }
            }
        }
    }

    /// <summary>Scales every sample in place by the gain of the channel it belongs to.</summary>
    private static unsafe void Scale(nint[] planes, bool interleaved, int channels, int samples, float[] gains)
    {
        if (interleaved)
        {
            float* start = (float*)planes[0];
            for (int sample = 0; sample < samples; sample++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    start[(sample * channels) + channel] *= gains[channel];
                }
            }

            return;
        }

        for (int channel = 0; channel < channels; channel++)
        {
            float* start = (float*)planes[channel];
            for (int sample = 0; sample < samples; sample++)
            {
                start[sample] *= gains[channel];
            }
        }
    }

    private sealed partial class MixedBuffer : IPlanarAudioBuffer
    {
        private readonly byte[] _bytes;
        private readonly GCHandle _handle;
        private bool _disposed;

        internal MixedBuffer(byte[] bytes)
        {
            _bytes = bytes;
            _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        }

        public int PlaneCount => 1;

        public long Length => _bytes.Length;

        public nint GetPlanePointer(int plane)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return plane == 0 ? _handle.AddrOfPinnedObject() : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A mixed block has one plane.");
        }

        public int GetPlaneByteCount(int plane)
        {
            return plane == 0 ? _bytes.Length : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A mixed block has one plane.");
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

    private sealed record MixState(
        ChannelLayout Layout,
        int ChannelCount,
        int TargetChannelCount,
        IReadOnlyDictionary<string, int>? RemixLevels,
        IReadOnlyDictionary<string, int>? DownmixLevels,
        FoldMatrix? Matrix,
        float[]? Gains);
}