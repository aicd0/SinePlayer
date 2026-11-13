// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Common.Playback.Core;

namespace SinePlayer.Data.Models.Misc;

internal sealed class PlayerSettingsModel
{
    public PlayerPresetSettingsModel Player { get; set; } = new();
    public VideoSettingsModel Video { get; set; } = new();
    public AudioSettingsModel Audio { get; set; } = new();
    public SubtitleSettingsModel Subtitle { get; set; } = new();

    public PlaybackComponents ToComponents()
    {
        return new(Video.Decoder, Video.Renderer, Audio.Decoder, Audio.Renderer, Subtitle.Decoder, Subtitle.Renderer, Video.PreferHardwareDecoding, Video.PreferHdr);
    }
}
