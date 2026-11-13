// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text.Json.Serialization;

namespace SinePlayer.Common.Playback.Core;

internal sealed class SelectedTrackModel
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("Track")]
    public TrackIdentityModel? Track { get; init; }
}
