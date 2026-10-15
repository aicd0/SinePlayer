// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface IVideoDecoderFactory : IComponentFactory
{
    /// <summary>
    /// Creates a decoder that is ready to decode. The source is required because the container holds
    /// the codec configuration the decoder needs; it is only read during this call.
    /// </summary>
    PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder);
}
