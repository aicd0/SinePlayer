// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

internal static class VideoSurfaceFactory
{
    internal static OperationResult<IVideoSurface> Create(D3D11Device device, int width, int height, double compositionScale, VideoSurfacePreference preference, ColourInfo content)
    {
        OperationResultCreator<IVideoSurface> result = new();

        if (ShouldTryHdr(preference, content))
        {
            OperationResult<Hdr10VideoSurface> hdr = Hdr10VideoSurface.TryCreate(
                device,
                width,
                height,
                compositionScale,
                DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE);

            if (hdr.IsSuccessful)
            {
                return result.Success(hdr.Result);
            }

            if (preference == VideoSurfacePreference.Hdr10)
            {
                return result.Failure(hdr);
            }
        }

        OperationResult<SdrVideoSurface> sdr = SdrVideoSurface.TryCreate(
            device,
            width,
            height,
            compositionScale,
            DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE);

        return sdr.IsSuccessful ? result.Success(sdr.Result) : result.Failure(sdr);
    }

    internal static bool ShouldTryHdr(VideoSurfacePreference preference, ColourInfo content)
    {
        if (preference == VideoSurfacePreference.Sdr)
        {
            return false;
        }

        if (preference == VideoSurfacePreference.Hdr10)
        {
            return true;
        }

        // A standard dynamic range picture gains nothing from the wider container: its white is placed at the reference
        // level and the rest follows, so it reads the same. Rebuilding the swap chain for it would be a change with no gain.
        if (!content.IsHighDynamicRange)
        {
            return false;
        }

        // A display that will not say is treated as one that cannot: showing a correct SDR picture is a better answer
        // than showing a PQ one to a screen that is not composing for it.
        OperationResult<DisplayHdrState> queried = DxgiDisplay.TryQuery();
        return queried.IsSuccessful && queried.Result.IsHdrComposed;
    }
}
