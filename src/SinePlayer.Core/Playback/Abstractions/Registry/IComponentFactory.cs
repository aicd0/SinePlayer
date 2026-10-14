// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Registry;

/// <summary>
/// A registered component. The registry stores factories rather than instances so that a candidate
/// which cannot be created for a particular stream can be skipped without failing playback.
/// </summary>
public interface IComponentFactory
{
    ComponentCapabilities Capabilities { get; }
}

/// <summary>
/// A factory for components that need no information about the stream in order to be created.
/// </summary>
public interface IComponentFactory<TComponent> : IComponentFactory
{
    PlaybackResult TryCreate(out TComponent? component);
}
