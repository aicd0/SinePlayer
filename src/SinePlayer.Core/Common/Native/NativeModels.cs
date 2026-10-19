// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Common.Native;

internal class NativeModels
{
    public delegate void WinEventDelegate(nint winEventHookHandle, uint eventType, nint windowHandle, int objectId, int childId, uint eventThreadId, uint eventTimeInMilliseconds);
}
