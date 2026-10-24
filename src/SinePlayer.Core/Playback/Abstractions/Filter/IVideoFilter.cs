// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

public interface IVideoFilter : IMediaFilter
{
    PixelFormat OutputFormat { get; }

    OperationResult Configure(VideoStreamInfo stream);

    OperationResult Apply(RawVideoFrame input, out RawVideoFrame output);
}
