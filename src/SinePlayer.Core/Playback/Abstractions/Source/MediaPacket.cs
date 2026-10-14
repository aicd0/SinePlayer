// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

namespace SinePlayer.Core.Playback.Abstractions.Source;

/// <summary>
/// One encoded packet read from a stream, with timestamps already converted to the engine's time
/// base. The packet owns <see cref="Buffer"/> and releases it when disposed.
/// </summary>
public sealed class MediaPacket : IDisposable
{
    public required int StreamIndex { get; init; }

    public required TimeSpan Timestamp { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool IsKeyFrame { get; init; }

    public required IMediaBuffer Buffer { get; init; }

    public void Dispose()
    {
        Buffer.Dispose();
    }
}
