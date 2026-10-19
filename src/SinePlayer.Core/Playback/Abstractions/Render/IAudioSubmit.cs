// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// Why a hand-off stopped and is asking to be resumed. It names the condition, not the answer: what to do
/// about it belongs to the caller, which is the only party that can also see the playback state and the
/// position.
/// </summary>
public enum AudioSubmitYield
{
    /// <summary>
    /// The device has no room for what is left of the block. It may be because the device is full, because it
    /// is paused, or because it has stopped taking audio altogether — a renderer cannot tell those apart, and
    /// the pipeline does not need it to: it waits on its own thread, where a pause, a seek, a stop and the
    /// device's own progress can all end the wait.
    /// </summary>
    NoRoom,
}

/// <summary>
/// One block being handed to an audio device, in progress. <see cref="MoveNext"/> writes what the device has
/// room for and returns; a hand-off that cannot continue says so instead of waiting, and the caller decides
/// what to do next — which is what keeps every wait in the pipeline on the thread that can be woken.
/// <para>
/// The contract, in the order the members are used:
/// </para>
/// <list type="number">
/// <item><see cref="MoveNext"/> is called until it returns false. A true answer means the hand-off is
/// suspended: <see cref="Current"/> says what it is waiting for. A false answer means it is over, and
/// <see cref="Result"/> says how.</item>
/// <item>The operation is single-use. Enumerating it twice would hand the same audio over twice.</item>
/// <item><see cref="IDisposable.Dispose"/> abandons a hand-off that is still suspended. The frames already
/// written stay in the device, because a renderer cannot take them back; the caller flushes the device before
/// it feeds it another position's audio.</item>
/// <item>A renderer never waits, sleeps or retries on its own. Its only recourse is a yield.</item>
/// <item>One operation is owned by one thread, and it is the thread that calls <see cref="MoveNext"/>.</item>
/// </list>
/// <para>
/// The one duty a renderer cannot delegate: a device that can accept an unbounded amount of audio must still
/// refuse to run ahead without bound — a queue-based backend implements its own high-water mark and yields
/// <see cref="AudioSubmitYield.NoRoom"/> when it is reached — because a hand-off that never yields turns the
/// pipeline into "decode the file into memory".
/// </para>
/// </summary>
public interface IAudioSubmit : IDisposable
{
    /// <summary>
    /// Resumes the hand-off, writing whatever the device has room for. False when it is over — completed or
    /// failed — with the outcome in <see cref="Result"/>; true when it stopped and is waiting for
    /// <see cref="Current"/>.
    /// </summary>
    bool MoveNext();

    /// <summary>What the suspended hand-off is waiting for. Only meaningful after <see cref="MoveNext"/> returned true.</summary>
    AudioSubmitYield Current { get; }

    /// <summary>The outcome of the whole hand-off. Only meaningful after <see cref="MoveNext"/> returned false.</summary>
    PlaybackOperationResult Result { get; }
}
