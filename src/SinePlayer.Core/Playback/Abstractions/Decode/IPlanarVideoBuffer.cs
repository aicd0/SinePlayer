// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// A video buffer held in system memory and described by planes. Software decoders produce this so a
/// renderer can upload the pixels itself, which is what keeps decoding independent of the graphics
/// device while still avoiding a copy inside the decoder.
/// </summary>
public interface IPlanarVideoBuffer : IMediaBuffer
{
    /// <summary>Number of planes the pixel format uses; packed formats report 1.</summary>
    int PlaneCount { get; }

    /// <summary>Address of the first byte of a plane. Valid until the buffer is disposed.</summary>
    nint GetPlanePointer(int plane);

    /// <summary>Distance in bytes between two consecutive rows of a plane.</summary>
    int GetPlaneStride(int plane);
}
