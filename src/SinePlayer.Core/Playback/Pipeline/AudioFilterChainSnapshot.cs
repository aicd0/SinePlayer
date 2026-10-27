// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Filter;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Pipeline;

/// <summary>
/// The chain as it was at one moment: the filters a block goes through, the format they leave it in, and the version
/// that moment was. It never changes, so a caller can hold one and run blocks through it while the host edits the
/// chain underneath — which is what makes running a block and deciding what the device plays the same answer rather
/// than two reads that can disagree.
/// <para>
/// It is also where the three questions about a block are answered: which filters see it, what shape it comes out in,
/// and what happens when a filter cannot transform it.
/// </para>
/// </summary>
public sealed class AudioFilterChainSnapshot
{
    private readonly IAudioFilter[] _filters;

    internal AudioFilterChainSnapshot(IAudioFilter[] filters, AudioStreamInfo? outputFormat, long version)
    {
        _filters = filters;
        OutputFormat = outputFormat;
        Version = version;
    }

    /// <summary>An empty chain that has not been told what is playing: no filters, no format, version zero.</summary>
    internal static AudioFilterChainSnapshot Empty { get; } = new([], null, 0);

    /// <summary>
    /// How many times the chain has been published, counting from its first preparation. Two snapshots of different
    /// versions may still hand on the same shape, so this answers "is there anything newer", not "has the format moved".
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// The format this snapshot hands blocks on in: the stream as each filter leaves it, and the stream itself when
    /// none of them changes it. It is null only before the chain has been told what is playing.
    /// </summary>
    public AudioStreamInfo? OutputFormat { get; }

    /// <summary>The filters a block goes through, in the order they run.</summary>
    public IReadOnlyList<IAudioFilter> Filters => _filters;

    /// <summary>
    /// Runs a block through the filters this snapshot holds. A block that goes through is taken over: what comes back
    /// is the caller's to dispose, which is the same instance when nothing transformed it and the last filter's output
    /// when something did — the blocks in between are released here, so a copy a filter makes is not a leak.
    /// <para>
    /// A filter that cannot transform the block fails the whole call, because what it would have produced is the shape
    /// everything after it — and the device — was prepared for; there is nothing sensible to hand on instead, and the
    /// input block is left to the caller to release.
    /// </para>
    /// </summary>
    internal OperationResult<RawAudioBuffer> TryApply(RawAudioBuffer input)
    {
        ArgumentNullException.ThrowIfNull(input);

        OperationResultCreator<RawAudioBuffer> result = new();
        RawAudioBuffer current = input;
        foreach (IAudioFilter filter in _filters)
        {
            RawAudioBuffer produced = current;
            OperationResult applied;

            try
            {
                applied = filter.Apply(current, out produced);
            }
            catch (Exception exception)
            {
                applied = OperationResult.Failure(exception);
            }

            if (!applied.IsSuccessful)
            {
                if (!ReferenceEquals(current, input))
                {
                    current.Dispose();
                }

                return result.Failure($"{filter.Name} could not transform the audio block: {applied.Message}", applied.Exception);
            }

            if (!ReferenceEquals(produced, current))
            {
                current.Dispose();
                current = produced;
            }
        }

        return result.Success(current);
    }
}
