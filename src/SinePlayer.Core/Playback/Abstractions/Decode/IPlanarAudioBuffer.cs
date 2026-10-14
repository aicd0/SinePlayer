// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// A block of decoded samples held in system memory and described by planes. Planar formats report one
/// plane per channel, interleaved formats a single plane, so a renderer can walk either without the
/// decoder having to convert or copy.
/// </summary>
public interface IPlanarAudioBuffer : IMediaBuffer
{
    /// <summary>Number of planes the sample format uses; interleaved formats report 1.</summary>
    int PlaneCount { get; }

    /// <summary>Address of the first byte of a plane. Valid until the buffer is disposed.</summary>
    nint GetPlanePointer(int plane);

    /// <summary>Bytes of sample data in one plane.</summary>
    int GetPlaneByteCount(int plane);
}
