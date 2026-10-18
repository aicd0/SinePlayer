// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Source;

/// <summary>
/// An opened media input: the stream table, packet reading, and seeking. Instances are produced by
/// a source provider, which is the extension point for new input kinds, so the pipeline never has to
/// know how the bytes are obtained.
/// </summary>
public interface IMediaSource : IDisposable
{
    MediaInfo Info { get; }

    bool CanSeek { get; }

    /// <summary>
    /// Moves the read position so that the next packet of every stream starts at or after
    /// <paramref name="position"/>. Timestamps of subsequent packets stay in the original time base.
    /// </summary>
    PlaybackOperationResult Seek(TimeSpan position);

    /// <summary>
    /// Reads the next packet. A successful result with a null packet signals the end of the stream,
    /// while an unsuccessful result reports a read error.
    /// </summary>
    PlaybackOperationResult TryReadPacket(out MediaPacket? packet);
}
