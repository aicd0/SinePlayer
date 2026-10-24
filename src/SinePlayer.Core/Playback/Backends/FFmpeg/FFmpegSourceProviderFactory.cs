// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.FFmpeg;

/// <summary>
/// Registers libavformat as the engine's media source. It accepts any locator because
/// FFmpeg brings its own protocol layer, which is what makes a local file and a network stream work
/// through the same component.
/// </summary>
public sealed class FFmpegSourceProviderFactory : ISourceProviderFactory
{
    public OperationResult TryOpen(MediaLocator locator, out IMediaSource? source)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (!FFmpegSource.TryOpen(locator, out FFmpegSource? ffmpegSource, out string error))
        {
            source = null;
            return OperationResult.Failure(error);
        }

        source = ffmpegSource;
        return OperationResult.Success();
    }
}
