// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// A GPU device a pipeline draws with, offered to the components that can put it to work. A decoder that
/// decodes on this device produces textures the renderer can sample without a copy, which is the whole
/// reason it is offered at all.
/// <para>
/// The lock is not an implementation detail of whoever holds the device: Direct3D protects each call on a
/// device context, not a sequence of them, and a decoded frame and a draw reach the same context from two
/// threads. Every use of the context takes the lock around the whole sequence, and the lock has to be
/// recursive because a caller may already hold it.
/// </para>
/// </summary>
public interface IGpuDevice
{
    /// <summary>Native device handle, borrowed: it is valid for as long as this object lives.</summary>
    nint NativeDevice { get; }

    /// <summary>Recursive lock guarding the device's immediate context.</summary>
    object DeviceLock { get; }
}
