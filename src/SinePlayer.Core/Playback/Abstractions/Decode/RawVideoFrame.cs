// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// A decoded video frame. <see cref="Buffer"/> is either system memory or, when
/// <see cref="IsHardwareBacked"/> is set, a GPU texture implementing <see cref="IGpuVideoBuffer"/>.
/// Because the payload is passed by reference, a frame travels from decoder to filter to renderer
/// without a CPU copy.
/// </summary>
public sealed class RawVideoFrame : IDisposable
{
    public required TimeSpan Timestamp { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required PixelFormat Format { get; init; }

    public required IMediaBuffer Buffer { get; init; }

    public bool IsHardwareBacked { get; init; }

    public void Dispose()
    {
        Buffer.Dispose();
    }
}
