// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// Backend-neutral description of how video pixels are laid out. Only formats the engine can hand
/// to a filter or a renderer are listed; a backend producing anything else must convert.
/// </summary>
public enum PixelFormat
{
    Unknown,
    Nv12,
    P010,
    P016,
    Yuv420P,
    Yuv420P10,
    Yuv422P,
    Yuv444P,
    Bgra8,
    Rgba8,
}
