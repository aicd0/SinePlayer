// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// Offers <see cref="NullVideoRenderer"/>s. It never refuses, so it is available on a machine with no usable
/// graphics device — which is the point: it is the video sink a test or a headless run can always have.
/// </summary>
public sealed class NullVideoRendererFactory : IVideoRendererFactory
{
    private readonly string _signature;

    public NullVideoRendererFactory(string signature = "null:video-renderer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult TryCreate(out IComponentCreator<IVideoRenderer>? creator)
    {
        creator = new Creator(_signature);
        return OperationResult.Success();
    }

    private sealed class Creator(string signature) : IComponentCreator<IVideoRenderer>
    {
        public string Signature => signature;

        public OperationResult Create(out IVideoRenderer? component)
        {
            component = new NullVideoRenderer();
            return OperationResult.Success();
        }
    }
}
