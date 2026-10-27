// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;

namespace SinePlayer.Core.Playback.Abstractions;

public interface IGpuDevice
{
    OperationResult CanDisplayGpuFrame(PixelFormat format);
}
