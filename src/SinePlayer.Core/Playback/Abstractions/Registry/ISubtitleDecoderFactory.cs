// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface ISubtitleDecoderFactory : IComponentFactory
{
    /// <summary>
    /// Offers a decoder for one subtitle track, or a creator that makes one.
    /// </summary>
    /// <param name="source">
    /// The file the track lives in, whose packets the decoder is handed — or null for a track that comes from a file of
    /// its own (<see cref="SubtitleStreamInfo.Path"/>), which the decoder opens and reads for itself. A sidecar is not a
    /// stream of the playing file, so there are no packets to be given.
    /// </param>
    OperationResult TryCreate(IMediaSource? source, SubtitleStreamInfo stream, out IComponentCreator<ISubtitleDecoder>? creator);
}
