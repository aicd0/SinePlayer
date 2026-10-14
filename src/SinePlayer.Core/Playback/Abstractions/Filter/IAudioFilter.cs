// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

public interface IAudioFilter : IMediaFilter
{
    /// <summary>
    /// Prepares the filter for a stream. A filter that cannot work with the stream reports failure
    /// and is removed from the chain rather than failing playback.
    /// </summary>
    PlaybackResult Configure(AudioStreamInfo stream);

    /// <summary>
    /// Transforms a buffer. The output may be the same instance as the input, which is how
    /// pass-through and in-place work avoid a copy; otherwise the caller owns the returned buffer
    /// and is responsible for disposing it.
    /// </summary>
    PlaybackResult Apply(RawAudioBuffer input, out RawAudioBuffer output);
}
