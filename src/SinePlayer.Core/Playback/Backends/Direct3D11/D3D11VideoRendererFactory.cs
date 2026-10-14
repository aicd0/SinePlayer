// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Registry;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Registers the Direct3D 11 renderer. A renderer owns the graphics device, so the device is created
/// once here and shared by every renderer instance this factory hands out.
/// </summary>
public sealed class D3D11VideoRendererFactory : IComponentFactory<IVideoRenderer>
{
    private readonly object _lock = new();
    private D3D11Device? _device;

    public ComponentCapabilities Capabilities { get; } = new()
    {
        Name = "d3d11",
        Description = "Direct3D 11 composition renderer.",
    };

    public PlaybackResult TryCreate(out IVideoRenderer? component)
    {
        component = null;

        lock (_lock)
        {
            if (_device is null)
            {
                if (!D3D11Device.TryCreate(out D3D11Device? device, out string deviceError))
                {
                    return PlaybackResult.Failure(deviceError);
                }

                _device = device;
            }

            if (!D3D11VideoRenderer.TryCreate(_device!, out D3D11VideoRenderer? renderer, out string rendererError))
            {
                return PlaybackResult.Failure(rendererError);
            }

            component = renderer;
            return PlaybackResult.Success;
        }
    }
}