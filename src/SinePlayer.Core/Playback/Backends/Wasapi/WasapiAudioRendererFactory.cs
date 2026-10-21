// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

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
/// What two renderers do share is the process's audio *session*, which is why nothing here touches the endpoint's
/// volume or mute: the level of the sound belongs to the pipeline's filter chain, where it is the same on every
/// renderer and cannot be lost by swapping one.
/// </para>
/// </summary>
public sealed class WasapiAudioRendererFactory : IAudioRendererFactory
{
    private readonly string? _endpointId;
    private readonly string? _endpointName;

    public WasapiAudioRendererFactory()
    {
    }

    private WasapiAudioRendererFactory(string endpointId, string endpointName)
    {
        _endpointId = endpointId;
        _endpointName = endpointName;
    }

    /// <summary>
    /// A factory that opens one particular output device rather than the default one — which is how a caller
    /// picks the device the sound comes out of.
    /// </summary>
    public static WasapiAudioRendererFactory For(WasapiAudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new WasapiAudioRendererFactory(endpoint.Id, endpoint.Name);
    }

    public PlaybackOperationResult TryCreate(out IComponentCreator<IAudioRenderer>? creator)
    {
        creator = new Creator(_endpointId, _endpointName);
        return PlaybackOperationResult.Success;
    }

    /// <summary>
    /// Opens the device on demand. Nothing here is opened by being offered, which is what lets a reconcile that
    /// changes nothing leave the device playing alone.
    /// </summary>
    private sealed class Creator(string? endpointId, string? endpointName) : IComponentCreator<IAudioRenderer>
    {
        /// <summary>The device this would open. Must match what the renderer reports for itself.</summary>
        public string Signature => endpointId is null ? "wasapi" : $"wasapi:{endpointId}";

        public PlaybackOperationResult Create(out IAudioRenderer? component)
        {
            component = new WasapiAudioRenderer(endpointId, endpointName);
            return PlaybackOperationResult.Success;
        }
    }
}
