// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

namespace SinePlayer.Core.Playback.Abstractions.Filter;

/// <summary>
/// Common contract of a processing stage. Filters are the insertion point for post-effects and are
/// the only stage that is expected to be chained: the pipeline runs them in order between the
/// decoder and the renderer.
/// </summary>
public interface IMediaFilter : IDisposable
{
    string Name { get; }
}
