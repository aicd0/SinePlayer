// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public enum ColourPrimaries
{
    Unknown,
    Bt709,
    Bt2020,
}

public enum ColourTransfer
{
    Unknown,
    Bt709,
    Smpte2084,
    Hlg,
}

public enum ColourRange
{
    Unknown,
    Limited,
    Full,
}

public readonly record struct ColourInfo(ColourPrimaries Primaries, ColourTransfer Transfer, ColourRange Range)
{
    public static ColourInfo Unknown => default;

    public bool IsHighDynamicRange => Transfer is ColourTransfer.Smpte2084 or ColourTransfer.Hlg;
}
