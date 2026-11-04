// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Info;

public sealed class MediaAttachment
{
    public required string Name { get; init; }

    public string? MimeType { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }
}
