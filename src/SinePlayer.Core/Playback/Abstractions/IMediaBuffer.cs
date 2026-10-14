// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// An opaque payload owned by a backend: encoded packet data, decoded samples, or a GPU texture.
/// Implementations live in the backend that produces them, and no native handle is exposed here.
/// </summary>
public interface IMediaBuffer : IDisposable
{
    /// <summary>Payload size in bytes, or 0 when the size is not meaningful for the payload kind.</summary>
    long Length { get; }
}
