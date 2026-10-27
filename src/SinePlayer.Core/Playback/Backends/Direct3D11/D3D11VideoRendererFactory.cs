// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
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
public sealed class D3D11VideoRendererFactory : IVideoRendererFactory
{
    private readonly string _signature;

    public D3D11VideoRendererFactory(string signature = "d3d11")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult TryCreate(out IComponentCreator<IVideoRenderer>? creator)
    {
        creator = new Creator(_signature);
        return OperationResult.Success();
    }

    /// <summary>
    /// Builds the device and the renderer on demand. The signature is the factory's, so what this would be is
    /// known without a device — which is what keeps a reconcile that changes nothing from creating one.
    /// </summary>
    private sealed class Creator(string signature) : IComponentCreator<IVideoRenderer>
    {
        public string Signature => signature;

        public OperationResult Create(out IVideoRenderer? component)
        {
            component = null;

            OperationResult<D3D11Device> device = D3D11Device.TryCreate();
            if (!device.IsSuccessful)
            {
                return OperationResult.Failure(device);
            }

            OperationResult<D3D11VideoRenderer> renderer = D3D11VideoRenderer.TryCreate(device.Result);
            if (!renderer.IsSuccessful)
            {
                // The renderer would have been the device's owner, so nothing else can release it.
                device.Result.Dispose();
                return OperationResult.Failure(renderer);
            }

            component = renderer.Result;
            return OperationResult.Success();
        }
    }
}