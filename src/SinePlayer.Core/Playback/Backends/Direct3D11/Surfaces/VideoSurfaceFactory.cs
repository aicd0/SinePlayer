// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Chooses the container a renderer presents into, and makes it.
/// <para>
/// The choice is about the display, not about the file: an HDR container carries an SDR picture as faithfully as an SDR
/// one does — its white is placed at a fixed light level and the rest follows — while an SDR container cannot carry an
/// HDR picture at all. So one answer serves every file, and it changes when the display's state changes rather than
/// when the next file opens.
/// </para>
/// <para>
/// A container is only chosen if it can actually be made. A chain that refuses the ten-bit format or the PQ colour space
/// is disposed and the SDR one is used instead, because the alternative is a surface whose meaning nothing established.
/// A host that asked for HDR specifically hears about the failure; a host that asked for <see cref="VideoSurfacePreference.Auto"/>
/// gets the picture it can have.
/// </para>
/// </summary>
internal static class VideoSurfaceFactory
{
    internal static OperationResult<IVideoSurface> TryCreate(D3D11Device device, int width, int height, double compositionScale, VideoSurfacePreference preference)
    {
        OperationResultCreator<IVideoSurface> result = new();

        if (preference != VideoSurfacePreference.Sdr && ShouldTryHdr(preference))
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

    /// <summary>Whether the display is composed for HDR, or whether the host insists regardless.</summary>
    internal static bool ShouldTryHdr(VideoSurfacePreference preference)
    {
        if (preference == VideoSurfacePreference.Sdr)
        {
            return false;
        }

        if (preference == VideoSurfacePreference.Hdr10)
        {
            return true;
        }

        // A display that will not say is treated as one that cannot: showing a correct SDR picture is a better answer
        // than showing a PQ one to a screen that is not composing for it.
        OperationResult<DisplayHdrState> queried = DxgiDisplay.TryQuery();
        return queried.IsSuccessful && queried.Result.IsHdrComposed;
    }
}
