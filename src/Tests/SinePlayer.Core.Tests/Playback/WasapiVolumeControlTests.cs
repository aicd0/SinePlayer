// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// What "this player's own volume" means, at the level the device is actually controlled. Two players are two
/// streams, and the level handed to each stream is the one its player asked for — never the session's, which would
/// move both. WASAPI has no per-stream mute, so mute is the level going to zero while the level the caller set is
/// left alone; that is what makes unmuting restore it instead of restoring silence.
/// </summary>
[TestFixture]
public class WasapiVolumeControlTests
{
    [Test]
    public void AppliesTheLevelItIsGiven()
    {
        FakeStreamVolume stream = new();
        WasapiVolumeControl control = new(stream);

        control.Apply(0.25f, muted: false);

        Assert.That(stream.Levels, Is.EqualTo(new[] { 0.25f }));
    }

    [Test]
    public void MutesByApplyingZeroAndRestoresTheLevelAfterwards()
    {
        FakeStreamVolume stream = new();
        WasapiVolumeControl control = new(stream);

        control.Apply(0.25f, muted: true);
        Assert.That(stream.Levels, Is.EqualTo(new[] { 0f }), "mute is the level going to zero, not a second control");

        control.Apply(0.25f, muted: false);
        Assert.That(stream.Levels, Is.EqualTo(new[] { 0f, 0.25f }), "unmuting restores the level the caller still holds");
    }

    [Test]
    public void ClampsALevelRatherThanRefusingIt()
    {
        FakeStreamVolume stream = new();
        WasapiVolumeControl control = new(stream);

        control.Apply(2.0f, muted: false);
        control.Apply(-1.0f, muted: false);

        Assert.That(stream.Levels, Is.EqualTo(new[] { 1f, 0f }), "a volume is a request, so it is brought into range");
    }

    private sealed class FakeStreamVolume : IWasapiStreamVolume
    {
        internal List<float> Levels { get; } = [];

        public void SetAllChannels(float level)
        {
            Levels.Add(level);
        }
    }
}
