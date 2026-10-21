// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// Offers <see cref="NullAudioRenderer"/>s. It never refuses, so it is the audio device a test or a headless run
/// can always have.
/// </summary>
public sealed class NullAudioRendererFactory : IAudioRendererFactory
{
    private readonly string _signature;

    public NullAudioRendererFactory(string signature = "null:audio-renderer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public PlaybackOperationResult TryCreate(out IComponentCreator<IAudioRenderer>? creator)
    {
        creator = new Creator(_signature);
        return PlaybackOperationResult.Success;
    }

    private sealed class Creator(string signature) : IComponentCreator<IAudioRenderer>
    {
        public string Signature => signature;

        public PlaybackOperationResult Create(out IAudioRenderer? component)
        {
            component = new NullAudioRenderer();
            return PlaybackOperationResult.Success;
        }
    }
}
