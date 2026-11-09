// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SinePlayer.Common.Playback.Core;

internal sealed class TrackSelectionModel
{
    [JsonPropertyName("AllowMultiAudioTracks")]
    public bool AllowMultiAudioTracks { get; set; }

    [JsonPropertyName("Views")]
    public List<ViewTrackSelectionModel>? Views { get; set; }

    [JsonPropertyName("AudioSelections")]
    public List<TrackSelectionEntryModel>? AudioSelections { get; set; }
}

internal sealed class ViewTrackSelectionModel
{
    [JsonPropertyName("AllowMultiSubtitleTracks")]
    public bool AllowMultiSubtitleTracks { get; set; }

    [JsonPropertyName("VideoSelection")]
    public SelectedTrackModel? VideoSelection { get; set; }

    [JsonPropertyName("SubtitleSelections")]
    public List<TrackSelectionEntryModel>? SubtitleSelections { get; set; }
}

internal sealed class TrackSelectionEntryModel
{
    [JsonPropertyName("Path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("Index")]
    public int Index { get; init; }
}
