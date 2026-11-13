// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Text.Json.Serialization;

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Common.Playback.Core;

internal sealed record TrackIdentityModel
{
    [JsonPropertyName("Path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("Index")]
    public int Index { get; init; }

    public static TrackIdentityModel From(MediaStreamInfo stream, string path)
    {
        return new()
        {
            Path = path,
            Index = stream.Index,
        };
    }
}
