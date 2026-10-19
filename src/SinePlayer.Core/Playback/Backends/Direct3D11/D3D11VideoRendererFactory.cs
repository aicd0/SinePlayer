// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Registers the Direct3D 11 renderer. Every call creates its own graphics device, and the renderer owns it from
/// then on.
/// <para>
/// The device cannot be shared, and this is why: a renderer draws through the device's <em>immediate context</em>,
/// and a frame is a sequence of calls on it — upload, set the output, bind the shaders, draw. D3D11 protects each
/// call, not the sequence, so two renderers on one context let one player's state changes land between the
/// other's setup and its draw, and a frame ends up composited into the wrong surface. A device per renderer makes
/// that impossible by construction, which is cheaper than a rule every caller has to remember: the cost is one
/// device per simultaneous player, and nothing is shared between them to lose.
/// </para>
/// </summary>
public sealed class D3D11VideoRendererFactory : IComponentFactory<IVideoRenderer>
{
    public ComponentCapabilities Capabilities { get; } = new()
    {
        Name = "d3d11",
        Description = "Direct3D 11 composition renderer.",
    };

    public PlaybackOperationResult TryCreate(out IVideoRenderer? component)
    {
        component = null;

        if (!D3D11Device.TryCreate(out D3D11Device? device, out string deviceError) || device is null)
        {
            return PlaybackOperationResult.Failure(
                string.IsNullOrEmpty(deviceError) ? "No Direct3D device was created." : deviceError);
        }

        if (!D3D11VideoRenderer.TryCreate(device, out D3D11VideoRenderer? renderer, out string rendererError))
        {
            // The renderer would have been the device's owner, so nothing else can release it.
            device.Dispose();
            return PlaybackOperationResult.Failure(rendererError);
        }

        component = renderer;
        return PlaybackOperationResult.Success;
    }
}