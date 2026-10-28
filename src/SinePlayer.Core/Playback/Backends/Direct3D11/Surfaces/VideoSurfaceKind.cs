// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

internal enum VideoSurfaceKind
{
    Sdr,
    Hdr10,
}

internal enum VideoSurfaceTransform
{
    None,
    ToneMapPqToSdr,
    ToneMapHlgToSdr,
    PassThroughPq,
    ConvertHlgToPq,
    EncodeSdrToPq,
}

internal static class VideoSurfaceTransforms
{
    internal static VideoSurfaceTransform Select(VideoSurfaceKind kind, ColourInfo content)
    {
        if (kind == VideoSurfaceKind.Hdr10)
        {
            return content.Transfer switch
            {
                ColourTransfer.Smpte2084 => VideoSurfaceTransform.PassThroughPq,
                ColourTransfer.Hlg => VideoSurfaceTransform.ConvertHlgToPq,
                _ => VideoSurfaceTransform.EncodeSdrToPq,
            };
        }

        return content.Transfer switch
        {
            ColourTransfer.Smpte2084 => VideoSurfaceTransform.ToneMapPqToSdr,
            ColourTransfer.Hlg => VideoSurfaceTransform.ToneMapHlgToSdr,
            _ => VideoSurfaceTransform.None,
        };
    }
}
