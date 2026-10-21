// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface IComponentRegistry
{
    IComponentRegistry Clone();

    IReadOnlyList<TFactory> GetComponents<TFactory>() where TFactory : class, IComponentFactory;

    void Register(IComponentFactory factory);

    bool Remove(IComponentFactory factory);

    int Clear<TFactory>() where TFactory : class, IComponentFactory;
}
