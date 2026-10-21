// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A block of silent samples in system memory, in the layout the renderer expects of the stream's own format.
/// <para>
/// One interleaved plane of zeroes: every signed and floating sample format reads zero as silence, and a renderer
/// that walks planes — WASAPI's does — takes the single plane as the interleaved layout it is. That is what lets a
/// null decoder's output be played by a <em>real</em> device: it is asked to play silence at the right moment
/// rather than being handed a payload it cannot read.
/// </para>
/// </summary>
internal sealed class NullPlanarAudioBuffer : IPlanarAudioBuffer
{
    private readonly byte[] _bytes;
    private readonly GCHandle _handle;
    private bool _disposed;

    internal NullPlanarAudioBuffer(AudioSampleFormat sampleFormat, int channelCount, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        if (!TryGetBytesPerSample(sampleFormat, out int bytesPerSample))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFormat), sampleFormat, "A null decoder cannot produce samples it has no size for.");
        }

        _bytes = new byte[bytesPerSample * channelCount * sampleCount];
        _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    /// <summary>How wide one sample of that format is, or false when the format describes no samples.</summary>
    internal static bool TryGetBytesPerSample(AudioSampleFormat sampleFormat, out int bytesPerSample)
    {
        bytesPerSample = sampleFormat switch
        {
            AudioSampleFormat.UInt8 => 1,
            AudioSampleFormat.Int16 => 2,
            AudioSampleFormat.Int24 => 3,
            AudioSampleFormat.Int32 => 4,
            AudioSampleFormat.Float32 => 4,
            AudioSampleFormat.Float64 => 8,
            _ => 0,
        };

        return bytesPerSample > 0;
    }

    public int PlaneCount => 1;

    public long Length => _bytes.Length;

    public nint GetPlanePointer(int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return plane == 0 ? _handle.AddrOfPinnedObject() : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A null audio buffer has one plane.");
    }

    public int GetPlaneByteCount(int plane)
    {
        return plane == 0 ? _bytes.Length : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A null audio buffer has one plane.");
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
}
