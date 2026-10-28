// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed class NullSubtitleRendererFactory : ISubtitleRendererFactory
{
    private readonly string _signature;

    public NullSubtitleRendererFactory(string signature = "null:subtitle-renderer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult<IComponentCreator<ISubtitleRenderer>> Create(SubtitleCueFormat format)
    {
        return new OperationResultCreator<IComponentCreator<ISubtitleRenderer>>().Success(new Creator(_signature));
    }

    private sealed class Creator(string signature) : IComponentCreator<ISubtitleRenderer>
    {
        public string Signature => signature;

        public OperationResult Create(out ISubtitleRenderer? component)
        {
            component = new NullSubtitleRenderer();
            return OperationResult.Success();
        }
    }
}
