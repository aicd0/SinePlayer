// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;

namespace SinePlayer.Core.Playback.Audio;

internal static class AudioVolumeScaler
{
    internal static OperationResult TryApply(IPlanarAudioBuffer planar, double gain)
    {
        if (gain == 1.0)
        {
            return OperationResult.Success();
        }

        int planeCount = planar.PlaneCount;
        if (planeCount <= 0)
        {
            return OperationResult.Failure("The audio buffer holds no planes.");
        }

        // Checked before anything is written: a block that cannot be scaled is left as it was found.
        for (int plane = 0; plane < planeCount; plane++)
        {
            if (planar.GetPlaneByteCount(plane) > 0 && planar.GetPlanePointer(plane) == 0)
            {
                return OperationResult.Failure($"Plane {plane} of the audio buffer has no memory.");
            }
        }

        for (int plane = 0; plane < planeCount; plane++)
        {
            int byteCount = planar.GetPlaneByteCount(plane);
            if (byteCount <= 0)
            {
                continue;
            }

            Scale(planar.GetPlanePointer(plane), byteCount / sizeof(float), gain);
        }

        return OperationResult.Success();
    }

    private static unsafe void Scale(nint pointer, int valueCount, double gain)
    {
        float* values = (float*)pointer;
        for (int index = 0; index < valueCount; index++)
        {
            values[index] = (float)(values[index] * gain);
        }
    }
}