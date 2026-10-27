// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

internal static class FFmpegHardwareBackends
{
    internal static OperationResult<IFFmpegHardwareBackend> TryCreate(IGpuDevice? device)
    {
        OperationResultCreator<IFFmpegHardwareBackend> result = new();

        if (device is ID3D11GpuDevice d3d11)
        {
            return FFmpegD3D11Hardware.TryCreate(d3d11);
        }

        if (device is not null)
        {
            return result.Failure($"FFmpeg has no decoder for the device a renderer offered ({device.GetType().Name}).");
        }

        return FFmpegD3D11Hardware.TryCreate(null);
    }
}
