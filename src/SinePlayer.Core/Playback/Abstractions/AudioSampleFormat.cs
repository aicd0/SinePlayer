// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions;

/// <summary>
/// Backend-neutral description of how audio samples are stored.
/// </summary>
public enum AudioSampleFormat
{
    Unknown,
    UInt8,
    Int16,
    Int24,
    Int32,
    Float32,
    Float64,
}
