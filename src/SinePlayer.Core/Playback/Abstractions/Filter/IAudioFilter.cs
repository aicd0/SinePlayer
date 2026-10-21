// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

public interface IAudioFilter : IMediaFilter
{
    /// <summary>
    /// Prepares the filter for a stream. A filter that cannot work with the stream reports failure
    /// and is removed from the chain rather than failing playback.
    /// </summary>
    PlaybackOperationResult Configure(AudioStreamInfo stream);

    /// <summary>
    /// Transforms a buffer. The output may be the same instance as the input, which is how
    /// pass-through and in-place work avoid a copy; otherwise the caller owns the returned buffer
    /// and is responsible for disposing it.
    /// <para>
    /// The chain is a straight line, so a filter must keep the format it is given: sample rate, channel count,
    /// channel layout and sample format are the stream's, and a filter that would have to change one of them
    /// should refuse in <see cref="Configure"/> instead of doing it here. That is what makes dropping a filter —
    /// at configuration or, later, because a block failed to transform — a matter of losing the effect and not
    /// of having to reconfigure anything downstream.
    /// </para>
    /// <para>
    /// <see cref="Configure"/> is called before the filter is handed a block — when a stream is opened, or when the
    /// filter is added to a chain that already has one — and <see cref="Apply"/> is called on the audio thread. The
    /// two are never in flight for the same filter at once, so nothing here needs a lock. A filter that is also
    /// controllable from outside — the gain filter's level, for instance, which its owner sets while it is
    /// transforming — keeps that control separate and thread-safe rather than reaching into the state these two
    /// methods use.
    /// </para>
    /// </summary>
    PlaybackOperationResult Apply(RawAudioBuffer input, out RawAudioBuffer output);
}
