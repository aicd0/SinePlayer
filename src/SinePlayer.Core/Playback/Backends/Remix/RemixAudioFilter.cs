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

    private readonly AtomicCell<MixState> _state = new(new MixState(ChannelLayout.Unknown, 0, ChannelLayout.Unknown, 0, null, null, 0.0f, false, null, null));

    private AudioStreamInfo? _outputFormat;
    private bool _disposed;

    public string Name => "Remix";

    public AudioStreamInfo? OutputFormat => _outputFormat;

    /// <summary>
    /// How far the sound is moved to one side, from -1 (all the way left) to 1: a negative value moves it left and
    /// quietens the right side, a positive one the other way. It is the last step of the mix, so the side it moves away
    /// from is silent at the extreme.
    /// </summary>
    public float Pan
    {
        get => _state.Read().Pan;
        set
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            float pan = float.IsFinite(value) ? Math.Clamp(value, -1.0f, 1.0f) : 0.0f;
            _state.Write(state => state.Pan == pan ? state : BuildMix(state with { Pan = pan }));
        }
    }

    /// <summary>
    /// Whether the two sides come out exchanged, for a device whose speakers are wired the other way round. The exchange
    /// happens before the balance, so the balance always works on the sides as the listener sees them.
    /// </summary>
    public bool InvertLeftAndRight
    {
        get => _state.Read().InvertLeftAndRight;
        set
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            _state.Write(state => state.InvertLeftAndRight == value ? state : BuildMix(state with { InvertLeftAndRight = value }));
        }
    }

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
                _state.Write(_ => new MixState(input.ChannelLayout, input.ChannelCount, target.ChannelLayout, target.ChannelCount, current.RemixLevels, current.DownmixLevels, current.Pan, current.InvertLeftAndRight, null, null));
                return OperationResult.Failure($"The {input.ChannelCount} channel layout '{input.ChannelLayout}' has no downmix to {target.ChannelCount} channel(s).");
            }

            _outputFormat = input.WithLayout(target.ChannelCount, target.ChannelCount == 1 ? ChannelLayout.Mono : ChannelLayout.Stereo);
        }
        else if (IsUpmix(input.ChannelCount, target.ChannelCount) && RemixMixer.TryCreateUpmix(input.ChannelLayout, input.ChannelCount, target.ChannelLayout, target.ChannelCount, current.RemixLevels, out _))
        {
            // A narrower stream is widened to the sides the target has, so that what the listener does with the balance
            // has somewhere to go.
            _outputFormat = input.WithLayout(target.ChannelCount, target.ChannelLayout);
        }

        MixState next = new(input.ChannelLayout, input.ChannelCount, target.ChannelLayout, target.ChannelCount, current.RemixLevels, current.DownmixLevels, current.Pan, current.InvertLeftAndRight, null, null);
        _state.Write(_ => BuildMix(next));
        return OperationResult.Success();
    }

    /// <summary>
    /// The levels of one set, as percentages of full scale keyed by group. A group that is absent keeps the default the
    /// mixer gives it; a value outside 0-200 is clamped. Nothing about the format changes, so the mix is rebuilt for the
    /// arrangement the filter was configured with and the next block uses it without the device being reopened.
    /// </summary>
    public void SetLevels(RemixLevelSet set, IReadOnlyDictionary<string, float> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        // Copied so the mix is built from levels that cannot change under the thread building it.
        IReadOnlyDictionary<string, float> snapshot = new Dictionary<string, float>(levels);
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
        if (matrix is null && gains is null && !state.InvertLeftAndRight)
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
        int expected = matrix?.InputChannelCount ?? gains?.Length ?? channels;
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
                ChannelLayout = outputChannels == 1 ? ChannelLayout.Mono : outputChannels == 2 ? ChannelLayout.Stereo : state.TargetLayout,
                SampleFormat = format,
                SampleCount = samples,
                Buffer = new MixedBuffer(mixed),
            };
            return OperationResult.Success();
        }

        if (state.InvertLeftAndRight)
        {
            SwapSides(planes, interleaved, channels, samples, state.Layout);
        }

        if (gains is not null)
        {
            Scale(planes, interleaved, channels, samples, gains);
        }

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
            if (!RemixMixer.TryCreateDownmix(state.Layout, state.ChannelCount, state.TargetChannelCount, state.DownmixLevels, out FoldMatrix fold))
            {
                return state with { Matrix = null, Gains = null };
            }

            if (state.InvertLeftAndRight)
            {
                ApplyInversion(fold, ChannelLayout.Stereo);
            }

            ApplyPan(fold, ChannelLayout.Stereo, state.Pan);
            return state with { Matrix = fold, Gains = null };
        }

        if (IsUpmix(state.ChannelCount, state.TargetChannelCount))
        {
            if (!RemixMixer.TryCreateUpmix(state.Layout, state.ChannelCount, state.TargetLayout, state.TargetChannelCount, state.RemixLevels, out FoldMatrix upmix))
            {
                return state with { Matrix = null, Gains = null };
            }

            if (state.InvertLeftAndRight)
            {
                ApplyInversion(upmix, state.TargetLayout);
            }

            ApplyPan(upmix, state.TargetLayout, state.Pan);
            return state with { Matrix = upmix, Gains = null };
        }

        float[]? sideGains = RemixMixer.TryCreatePanSides(state.Layout, state.ChannelCount, state.Pan);
        if (RemixMixer.TryCreateRemix(state.Layout, state.ChannelCount, state.RemixLevels, out float[] trimmed))
        {
            for (int channel = 0; channel < trimmed.Length; channel++)
            {
                trimmed[channel] *= sideGains?[channel] ?? 1.0f;
            }

            return state with { Matrix = null, Gains = trimmed };
        }

        // A balance needs no levels of its own: an arrangement the table does not describe is still panned.
        return state with { Matrix = null, Gains = sideGains };
    }

    /// <summary>
    /// The balance is the last step of the mix, so it applies to the channels as they come out: whatever a side carries
    /// — a folded centre included — is quietened with it, and a channel that belongs to neither keeps its level.
    /// </summary>
    private static void ApplyInversion(FoldMatrix matrix, ChannelLayout layout)
    {
        int[] exchange = RemixMixer.CreateInversion(layout, matrix.ChannelCount);
        for (int output = 0; output < matrix.ChannelCount; output++)
        {
            int other = exchange[output];
            if (other <= output || other >= matrix.ChannelCount)
            {
                continue;
            }

            float[] swap = matrix.Rows[output];
            matrix.Rows[output] = matrix.Rows[other];
            matrix.Rows[other] = swap;
        }
    }

    /// <summary>Exchanges the samples of the channels a pair of sides is made of, where the block already holds them.</summary>
    private static unsafe void SwapSides(nint[] planes, bool interleaved, int channels, int samples, ChannelLayout layout)
    {
        int[] exchange = RemixMixer.CreateInversion(layout, channels);
        for (int channel = 0; channel < channels; channel++)
        {
            int other = exchange[channel];
            if (other <= channel || other >= channels)
            {
                continue;
            }

            if (interleaved)
            {
                float* start = (float*)planes[0];
                for (int sample = 0; sample < samples; sample++)
                {
                    int first = (sample * channels) + channel;
                    int second = (sample * channels) + other;
                    float swap = start[first];
                    start[first] = start[second];
                    start[second] = swap;
                }

                continue;
            }

            float* side = (float*)planes[channel];
            float* opposite = (float*)planes[other];
            for (int sample = 0; sample < samples; sample++)
            {
                float swap = side[sample];
                side[sample] = opposite[sample];
                opposite[sample] = swap;
            }
        }
    }

    /// <summary>
    /// The balance is the last step of the mix, so it applies to the channels as they come out: whatever a side carries
    /// — a folded centre included — is quietened with it, and a channel that belongs to neither keeps its level.
    /// </summary>
    private static void ApplyPan(FoldMatrix matrix, ChannelLayout layout, float pan)
    {
        if (pan == 0.0f || matrix.ChannelCount < 2)
        {
            return;
        }

        float[]? sides = RemixMixer.TryCreatePanSides(layout, matrix.ChannelCount, pan);
        if (sides is null)
        {
            return;
        }

        for (int output = 0; output < matrix.ChannelCount; output++)
        {
            float gain = sides[output];
            if (gain == 1.0f)
            {
                continue;
            }

            float[] coefficients = matrix.Rows[output];
            for (int input = 0; input < coefficients.Length; input++)
            {
                coefficients[input] *= gain;
            }
        }
    }

    /// <summary>Whether a stream of this width is folded down to the target rather than trimmed in place.</summary>
    private static bool IsDownmix(int channelCount, int targetChannelCount)
    {
        return channelCount > targetChannelCount && targetChannelCount <= TargetChannelCount;
    }

    /// <summary>Whether the target has sides a one-channel stream has to be widened to reach.</summary>
    private static bool IsUpmix(int channelCount, int targetChannelCount)
    {
        return channelCount < targetChannelCount;
    }

    private static unsafe void Fold(nint[] planes, bool interleaved, int channels, int samples, FoldMatrix matrix, byte[] destination)
    {
        int outputChannels = matrix.ChannelCount;
        int frameBytes = sizeof(float) * outputChannels;
        fixed (byte* target = destination)
        {
            double* sums = stackalloc double[outputChannels];
            for (int sample = 0; sample < samples; sample++)
            {
                for (int output = 0; output < outputChannels; output++)
                {
                    sums[output] = 0.0;
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    float* address = interleaved
                        ? (float*)planes[0] + ((sample * channels) + channel)
                        : (float*)planes[channel] + sample;
                    double value = *address;
                    for (int output = 0; output < outputChannels; output++)
                    {
                        sums[output] += value * matrix.Rows[output][channel];
                    }
                }

                float* destinationFrame = (float*)(target + ((long)sample * frameBytes));
                for (int output = 0; output < outputChannels; output++)
                {
                    destinationFrame[output] = (float)sums[output];
                }
            }
        }
    }

    /// <summary>Scales every sample in place by the gain its channel carries, leaving the ones at full level alone.</summary>
    private static unsafe void Scale(nint[] planes, bool interleaved, int channels, int samples, float[] gains)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            float gain = gains[channel];
            if (gain == 1.0f)
            {
                continue;
            }

            float* start = interleaved ? (float*)planes[0] + channel : (float*)planes[channel];
            int stride = interleaved ? channels : 1;
            for (int sample = 0; sample < samples; sample++)
            {
                start[sample * stride] *= gain;
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
        ChannelLayout TargetLayout,
        int TargetChannelCount,
        IReadOnlyDictionary<string, float>? RemixLevels,
        IReadOnlyDictionary<string, float>? DownmixLevels,
        float Pan,
        bool InvertLeftAndRight,
        FoldMatrix? Matrix,
        float[]? Gains);
}