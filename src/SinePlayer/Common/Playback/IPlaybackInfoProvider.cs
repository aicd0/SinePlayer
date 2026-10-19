// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback;

/// <summary>
/// The engine's technical account of what it is playing, as a surface beside the port reads it: the file's own
/// description, what the decoders that were actually chosen said about themselves, and the live state and counters.
/// <para>
/// Every member is read at the moment it is asked for, so a reader that refreshes on a timer sees the seek, the
/// stall or the new decoder. The hardware flags are the decoders' own answer rather than the stream's, because the
/// same stream can be decoded either way — which is the whole of scenario 1, and what a decoder switch changes.
/// </para>
/// <para>
/// This is a handle rather than the engine: after the player that made it is gone it answers "nothing" — null, zero
/// or <see cref="MediaEngineState.None"/> — and reaches nothing behind it.
/// </para>
/// </summary>
internal interface IPlaybackInfoProvider
{
    /// <summary>What is open, or null when nothing is.</summary>
    MediaInfo? Media { get; }

    MediaEngineState State { get; }

    /// <summary>Why playback failed, when the state is <see cref="MediaEngineState.Error"/>.</summary>
    string? Error { get; }

    TimeSpan Position { get; }

    long PresentedFrames { get; }

    long DroppedFrames { get; }

    /// <summary>Frames the compositor actually took, when the renderer can tell; null otherwise.</summary>
    long? DisplayedFrames { get; }

    /// <summary>How far the shown picture is behind the clock.</summary>
    TimeSpan PictureLag { get; }

    double AverageDecodeMilliseconds { get; }

    double AverageRenderMilliseconds { get; }

    /// <summary>Which stage playback is waiting for, or null when it is not waiting.</summary>
    string? StarvedFor { get; }

    double Volume { get; }

    bool IsMuted { get; }

    bool IsVideoHardwareAccelerated { get; }

    bool IsAudioHardwareAccelerated { get; }
}
