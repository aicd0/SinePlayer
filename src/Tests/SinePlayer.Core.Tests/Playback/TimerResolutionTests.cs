// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;

using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The request the engine makes so that its millisecond waits end when they were asked to, rather than on the system's
/// own tick of about fifteen and a half milliseconds — most of a frame at sixty hertz, which is long enough for a frame
/// waiting for its moment to be passed by the clock and dropped.
/// </summary>
[TestFixture]
public class TimerResolutionTests
{
    [Test]
    public void HoldsTheFinerResolutionUntilTheLastHolderGivesItBack()
    {
        int before = TimerResolution.Holders;

        TimerResolution.Hold();
        TimerResolution.Hold();
        Assert.That(TimerResolution.IsRaised, Is.True, "playback has asked for the finer resolution");
        Assert.That(TimerResolution.Holders, Is.EqualTo(before + 2));

        TimerResolution.Release();
        Assert.That(TimerResolution.IsRaised, Is.True, "one holder is still playing");

        TimerResolution.Release();
        Assert.That(TimerResolution.Holders, Is.EqualTo(before));
        if (before == 0)
        {
            Assert.That(TimerResolution.IsRaised, Is.False, "the last holder leaving is what gives it back");
        }
    }

    /// <summary>
    /// What the request is for: the same wait, asked for a millisecond figure, ends when it was asked to. The default
    /// process resolution rounds a fourteen millisecond wait up to the fifteen and a half millisecond tick, so a p90
    /// near that tick is what the request being absent looks like.
    /// </summary>
    [Test]
    public void MakesMillisecondWaitsEndOnTime()
    {
        List<double> overshoots = [];
        object gate = new();

        TimerResolution.Hold();
        try
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                long started = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    Monitor.Wait(gate, 14);
                }

                overshoots.Add(((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency) - 14.0);
                Thread.Sleep(20);
            }
        }
        finally
        {
            TimerResolution.Release();
        }

        overshoots.Sort();
        double p90 = overshoots[(int)(overshoots.Count * 0.9)];
        Assert.That(p90, Is.LessThan(5.0), $"a fourteen millisecond wait should end on time; p90 overshoot was {p90:0.0} ms: {string.Join(", ", overshoots.Select(value => value.ToString("0.0")))}");
    }
}
