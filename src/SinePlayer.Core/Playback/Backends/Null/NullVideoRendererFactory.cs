// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed class NullVideoRendererFactory : IVideoRendererFactory
{
    private readonly string _signature;

    public NullVideoRendererFactory(string signature = "null:video-renderer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult<IComponentCreator<IVideoRenderer>> Create()
    {
        return new OperationResultCreator<IComponentCreator<IVideoRenderer>>().Success(new Creator(_signature));
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
