// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// Creation settings handed to a decoder that has already been chosen. Which decoder gets chosen is a
/// separate concern handled by component selection, so this type carries only the knobs the chosen
/// implementation needs.
/// </summary>
public sealed class DecoderOptions
{
    public static DecoderOptions Default { get; } = new();

    /// <summary>Threads to decode with; 0 lets the backend decide.</summary>
    public int ThreadCount { get; init; } = 0;

    /// <summary>
    /// Whether the decoder must decode on the GPU. A requirement, not a hint: a decoder that would copy frames
    /// back to memory has to fail instead of returning them, so the caller that asked can decide what to give up
    /// — the resolver asks again without it, and a host that wanted hardware for its own reasons gets an honest
    /// answer rather than a slow surprise.
    /// </summary>
    public bool HardwareDecoding { get; init; } = true;

    /// <summary>
    /// The device to decode on and the lock that guards it, or null to let the backend create a device of
    /// its own. Decoding on the device the frames are drawn with is what keeps a decoded texture usable
    /// without a copy, so a pipeline that has one hands it here.
    /// </summary>
    public IGpuDevice? GpuDevice { get; init; }

    /// <summary>
    /// How many decoded frames the caller may be holding at once; 0 when it holds nothing beyond the frame
    /// it is given. A hardware decoder allocates those frames from a pool it owns, and a frame the caller
    /// still holds cannot be reused — so a decoder that is not told this runs its pool dry and fails to
    /// allocate a frame instead of waiting.
    /// </summary>
    public int BufferedFrames { get; init; } = 0;
}
