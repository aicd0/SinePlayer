// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Lifecycle.Utils;

public sealed class AlwaysActiveLifecycleOwner : ILifecycleOwner
{
    public static AlwaysActiveLifecycleOwner Instance { get; } = new();

    private readonly SimpleLifecycle _lifecycle = new();

    private AlwaysActiveLifecycleOwner()
    {
        _lifecycle.SetState(ILifecycle.State.Resumed);
    }

    public ILifecycle GetLifecycle()
    {
        return _lifecycle;
    }
}
