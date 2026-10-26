// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// What a decoded cue is made of, in terms a renderer can accept or refuse. It is a property of the decoder — every text
/// format is decoded into an ASS event line, whatever the container called it — and the chain is only assembled from a
/// decoder and a renderer that agree on it.
/// <para>
/// New values are added at the end and every side that meets an unfamiliar one must refuse rather than guess, so that an
/// unfamiliar kind of cue fails to be selected instead of being drawn wrongly.
/// </para>
/// </summary>
public enum SubtitleCueFormat
{
    /// <summary>Text as an ASS event line: layer, style, timing, then the text with its override tags.</summary>
    Ass,

    /// <summary>
    /// An image, 32 bits per pixel in blue-green-red-alpha order with the colour already multiplied by the alpha.
    /// </summary>
    BitmapBgra,
}
