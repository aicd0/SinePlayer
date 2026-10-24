// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Pipeline;

internal partial class PipelineComponent<T>(T component, string signature) : IDisposable where T : class, IDisposable
{
    public T Component { get; } = component;

    public string Signature { get; } = signature;

    public void Dispose()
    {
        Component.Dispose();
    }
}
