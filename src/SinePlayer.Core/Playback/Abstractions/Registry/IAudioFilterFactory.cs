// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Filter;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface IAudioFilterFactory : IComponentFactory
{
    PlaybackOperationResult TryCreate(out IAudioFilter? component);
}