// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Timing;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// Plays decoded audio on an output device. Because the device knows how much audio has actually
/// been heard, this stage also provides the pipeline clock whenever audio is present.
/// <para>
/// Lifetime: the renderer belongs to the pipeline that asked a factory for it, is configured per open and
/// disposed when that open ends, and is used by one thread at a time. Two renderers are independent — separate
/// streams, separate clocks — except for the session the operating system gives the process, which owns volume
/// and mute.
/// </para>
/// </summary>
public interface IAudioRenderer : IDisposable
{
    string Name { get; }

    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Clock driven by this renderer, used as the timing master while audio plays.</summary>
    IPlaybackClock Clock { get; }

    /// <summary>
    /// Audio queued in the device but not yet played, in frames of the configured stream. What it answers is
    /// whether the device still has something of ours to play: a device that holds nothing has played out
    /// what it was given, which is the sound's word about the end of the file. It is not a room estimate —
    /// whether a hand-off can continue is the hand-off's to report.
    /// </summary>
    long QueuedSampleCount { get; }

    PlaybackOperationResult Configure(AudioStreamInfo stream);

    /// <summary>
    /// Starts handing a block to the device, returning the hand-off in progress. The caller keeps ownership
    /// of the buffer and must keep it valid until the hand-off is over — completed or disposed — though a
    /// renderer that copies eagerly may ignore it after this call.
    /// <para>
    /// This call must not wait for the device. A device with no room is reported through
    /// <see cref="AudioSubmitYield.NoRoom"/> from <see cref="IAudioSubmit.MoveNext"/>, so the pipeline can
    /// wait on its own thread, where a pause, a seek and a stop can end the wait; a renderer's own wait is
    /// reachable by none of them.
    /// </para>
    /// <para>
    /// A hand-off may start while the clock is stopped: a stopped device takes what it is given and plays it
    /// once it is started. It must not start or stop the device itself — the running state of
    /// <see cref="Clock"/> is the pipeline's decision, and a hand-off that started the stream would release a
    /// hold on the clock that the picture depends on.
    /// </para>
    /// <para>
    /// One instance is used by one thread: neither this call nor the hand-off it returns is safe to use
    /// concurrently, including against <see cref="Flush"/>.
    /// </para>
    /// </summary>
    IAudioSubmit Submit(RawAudioBuffer buffer);

    /// <summary>Drops everything queued, for example after a seek.</summary>
    PlaybackOperationResult Flush();
}
