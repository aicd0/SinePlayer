// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// A video buffer whose payload is a GPU texture rather than system memory. Exposing the texture
/// here is what lets hardware frames be filtered and rendered on the GPU, and it keeps the GPU
/// resource a shared engine concept instead of a private detail of one backend.
/// </summary>
public interface IGpuVideoBuffer : IMediaBuffer
{
    /// <summary>Native <c>ID3D11Texture2D</c> pointer shared with the renderer of the same pipeline.</summary>
    nint NativeTexture { get; }

    /// <summary>Slice of the texture array that holds this frame.</summary>
    int SubresourceIndex { get; }
}
