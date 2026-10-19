// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// Backend-neutral description of how channels are arranged in an audio block. Channel order
/// follows the usual convention of the listed layout.
/// </summary>
public enum ChannelLayout
{
    Unknown,
    Mono,
    Stereo,
    Surround21,
    Quad,
    Surround50,
    Surround51,
    Surround71,
}
