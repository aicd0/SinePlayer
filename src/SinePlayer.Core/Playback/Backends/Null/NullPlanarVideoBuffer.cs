// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A black frame in system memory, small enough that producing one per packet costs nothing.
/// <para>
/// It exists so that a null decoder's output can be handed to a <em>real</em> renderer: an upload needs readable
/// planes, and a null decoder that produced an empty payload would fail there instead of showing anything. What
/// the renderer draws is a small black picture stretched over the surface, which is exactly what "nothing is being
/// decoded" should look like.
/// </para>
/// <para>
/// The size is fixed rather than the stream's: the renderer re-creates its plane textures when the frame size
/// changes, and a constant keeps the cost constant — 384 bytes, whatever the file is.
/// </para>
/// </summary>
internal sealed class NullPlanarVideoBuffer : IPlanarVideoBuffer
{
    internal const int Width = 16;
    internal const int Height = 16;

    private const int LumaByteCount = Width * Height;
    private const int ChromaByteCount = Width / 2 * (Height / 2);

    /// <summary>Limited-range black: luma at the bottom of the scale, chroma neutral.</summary>
    private const byte BlackLuma = 16;

    private const byte NeutralChroma = 128;

    private readonly byte[] _bytes = new byte[LumaByteCount + (2 * ChromaByteCount)];
    private readonly GCHandle _handle;
    private bool _disposed;

    internal NullPlanarVideoBuffer()
    {
        _bytes.AsSpan(0, LumaByteCount).Fill(BlackLuma);
        _bytes.AsSpan(LumaByteCount).Fill(NeutralChroma);
        _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    public int PlaneCount => 3;

    public long Length => _bytes.Length;

    public nint GetPlanePointer(int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handle.AddrOfPinnedObject() + PlaneOffset(plane);
    }

    public int GetPlaneStride(int plane)
    {
        return plane == 0 ? Width : Width / 2;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Free();
    }

    private static int PlaneOffset(int plane)
    {
        return plane switch
        {
            0 => 0,
            1 => LumaByteCount,
            2 => LumaByteCount + ChromaByteCount,
            _ => throw new ArgumentOutOfRangeException(nameof(plane), plane, "A YUV420P buffer has three planes."),
        };
    }
}
