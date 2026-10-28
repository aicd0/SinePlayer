// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

public sealed class D3D11VideoRendererFactory : IVideoRendererFactory
{
    private readonly string _signature;
    private readonly VideoSurfacePreference _surfacePreference;

    public D3D11VideoRendererFactory(VideoSurfacePreference surfacePreference)
    {
        _surfacePreference = surfacePreference;
        _signature = $"d3d11:{surfacePreference}";
    }

    public OperationResult TryCreate(out IComponentCreator<IVideoRenderer>? creator)
    {
        creator = new Creator(_signature, _surfacePreference);
        return OperationResult.Success();
    }

    private sealed class Creator(string signature, VideoSurfacePreference surfacePreference) : IComponentCreator<IVideoRenderer>
    {
        public string Signature => signature;

        public OperationResult Create(out IVideoRenderer? component)
        {
            component = null;

            OperationResult<D3D11Device> deviceCreated = D3D11Device.Create();
            if (!deviceCreated.IsSuccessful)
            {
                return OperationResult.Failure(deviceCreated);
            }

            D3D11Device device = deviceCreated.Result;
            OperationResult<D3D11VideoRenderer> rendererCreated = D3D11VideoRenderer.Create(device, surfacePreference);
            if (!rendererCreated.IsSuccessful)
            {
                device.Dispose();
                return OperationResult.Failure(rendererCreated);
            }

            component = rendererCreated.Result;
            return OperationResult.Success();
        }
    }
}