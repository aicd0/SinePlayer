// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface ISubtitleDecoderFactory : IComponentFactory
{
    OperationResult<IComponentCreator<ISubtitleDecoder>> Create(IMediaSource? source, SubtitleStreamInfo stream);
}
