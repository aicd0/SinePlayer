// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct AVD3D11VADeviceContext
{
    public nint Device;
    public nint DeviceContext;
    public nint VideoDevice;
    public nint VideoContext;
    public nint Lock;
    public nint Unlock;
    public nint LockContext;
}
