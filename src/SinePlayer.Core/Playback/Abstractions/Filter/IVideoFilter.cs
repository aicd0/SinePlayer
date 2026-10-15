// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

public interface IVideoFilter : IMediaFilter
{
    /// <summary>Format this filter produces. Meaningful only after a successful configure.</summary>
    PixelFormat OutputFormat { get; }

    /// <summary>
    /// Prepares the filter for a stream. A filter that cannot work with the stream reports failure
    /// and is removed from the chain rather than failing playback.
    /// </summary>
    PlaybackOperationResult Configure(VideoStreamInfo stream);

    /// <summary>
    /// Transforms a frame. The output may be the same instance as the input, which is how
    /// pass-through and in-place work avoid a copy; otherwise the caller owns the returned frame and
    /// is responsible for disposing it.
    /// </summary>
    PlaybackOperationResult Apply(RawVideoFrame input, out RawVideoFrame output);
}
