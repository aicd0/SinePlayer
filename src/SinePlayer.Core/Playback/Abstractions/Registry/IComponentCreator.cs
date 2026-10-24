// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Core.Playback.Abstractions.Registry;

public interface IComponentCreator<TComponent> where TComponent : class
{
    string Signature { get; }

    OperationResult Create(out TComponent? component);
}
