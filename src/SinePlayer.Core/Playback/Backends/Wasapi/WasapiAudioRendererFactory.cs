// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Registry;

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// Hands out WASAPI audio renderers, one per call.
/// <para>
/// Unlike the graphics device, nothing here is shared: each renderer opens its own stream on the default
/// endpoint, and shared mode mixes them, so every pipeline that creates a renderer plays its own sound at its
/// own position. That is why there is no cached instance to hand back — a renderer is stateful per device
/// stream, and two pipelines sharing one would share its clock too.
/// </para>
/// <para>
/// What two renderers do share is the process's audio *session*: the endpoint volume and mute belong to the
/// session, so per-pipeline volume would need <c>IAudioStreamVolume</c> or a session GUID per renderer. Until
/// then the pipeline's volume is the session's volume.
/// </para>
/// </summary>
public sealed class WasapiAudioRendererFactory : IComponentFactory<IAudioRenderer>
{
    public ComponentCapabilities Capabilities { get; } = new()
    {
        Name = "wasapi",
        Description = "Plays audio on the default output device through WASAPI in shared mode.",
    };

    public PlaybackOperationResult TryCreate(out IAudioRenderer? component)
    {
        component = new WasapiAudioRenderer();
        return PlaybackOperationResult.Success;
    }
}
