// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Engine;

internal partial class PipelineComponent<T>(
    T component,
    MediaStreamInfo stream,
    string signature) : IDisposable where T : class
{
    public T Component { get; } = component;

    public MediaStreamInfo Stream { get; } = stream;

    public string Signature { get; } = signature;

    public void Dispose()
    {
        (Component as IDisposable)?.Dispose();
    }
}
