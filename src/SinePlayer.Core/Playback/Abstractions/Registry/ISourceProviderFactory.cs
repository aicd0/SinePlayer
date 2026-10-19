// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// Opens a locator into a media source. This is the extension point for new input kinds, so adding
/// one never requires a change in the pipeline.
/// </summary>
public interface ISourceProviderFactory : IComponentFactory
{
    PlaybackOperationResult TryOpen(MediaLocator locator, out IMediaSource? source);
}
