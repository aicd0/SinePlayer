// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// Offers <see cref="NullSubtitleRenderer"/>s. It never refuses, because it draws nothing and so there is no format it
/// cannot leave undrawn — which is the point: it is the subtitle sink a test or a headless run can always have.
/// </summary>
public sealed class NullSubtitleRendererFactory : ISubtitleRendererFactory
{
    private readonly string _signature;

    public NullSubtitleRendererFactory(string signature = "null:subtitle-renderer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        _signature = signature;
    }

    public OperationResult TryCreate(SubtitleCueFormat format, out IComponentCreator<ISubtitleRenderer>? creator)
    {
        // The format is not part of the identity here: the renderer draws nothing whatever it is handed.
        creator = new Creator(_signature);
        return OperationResult.Success();
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
