// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Decode;

/// <summary>
/// Common contract of every decoder. An instance decodes exactly one stream and is driven from a
/// single thread, so backends do not need internal locking.
/// <para>
/// A decoder is created ready to use: the factory receives the opened source, because a decoder
/// cannot be built without the container's codec configuration (extradata for most modern codecs).
/// That dependency is real, and making it explicit prevents a decoder from assuming configuration it
/// does not have.
/// </para>
/// </summary>
public interface IMediaDecoder : IDisposable
{
    /// <summary>The stream this decoder was created for.</summary>
    MediaStreamInfo Stream { get; }

    /// <summary>Whether the active implementation actually runs on hardware.</summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>Drops buffered state and delayed frames, for example after a seek.</summary>
    PlaybackOperationResult Flush();
}
