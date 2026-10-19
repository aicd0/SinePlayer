// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

namespace SinePlayer.Core.Playback.Backends.Wasapi;

/// <summary>
/// The two device objects the clock needs, behind one seam: what a test can replace to make the device fail,
/// because a real endpoint can only be made to vanish by unplugging it.
/// <para>
/// Every member may throw: an endpoint can be invalidated at any moment (unplugged, disabled, pre-empted, or
/// re-formatted), and the clock's job is to survive that rather than pass it on to a thread that cannot
/// answer it.
/// </para>
/// </summary>
internal interface IWasapiClockDevice
{
    void Start();

    void Stop();

    /// <summary>How far the stream has travelled, in the clock's own frequency units.</summary>
    ulong Position { get; }
}
