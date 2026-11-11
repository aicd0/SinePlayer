// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Engine;

[Flags]
internal enum ChainChange
{
    None = 0,
    Source = 1,
    Decoder = 2,
    Renderer = 4,
}