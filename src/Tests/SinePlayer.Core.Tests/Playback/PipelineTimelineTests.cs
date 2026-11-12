// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The timeline's own clock is the authority for a pipeline with no audio to follow, and the thing the picture is timed
/// against either way. These tests pin what changing its rate means: it reports the rate, retiming never moves the position,
/// and the position advances at the rate afterwards.
/// </summary>
[TestFixture]
public class PipelineTimelineTests
{
    private const int RateWindowMilliseconds = 150;

    [Test]
    public void ReportsOneUntilItIsToldOtherwise()
    {
        PipelineTimeline timeline = new(() => { }, () => false);

        Assert.That(timeline.Rate, Is.EqualTo(1.0), "playback starts at its own speed");

        timeline.SetRate(2.5);
        Assert.That(timeline.Rate, Is.EqualTo(2.5));
    }

    [Test]
    public void RefusesARateThatIsNotOne()
    {
        // The clock treats a rate it cannot use as no rate at all, rather than stopping time or running it backwards; the
        // range a user may ask for is the pipeline's business, not the clock's.
        PipelineTimeline timeline = new(() => { }, () => false);

        using (Assert.EnterMultipleScope())
        {
            timeline.SetRate(0.0);
            Assert.That(timeline.Rate, Is.EqualTo(1.0));

            timeline.SetRate(-2.0);
            Assert.That(timeline.Rate, Is.EqualTo(1.0));

            timeline.SetRate(double.NaN);
            Assert.That(timeline.Rate, Is.EqualTo(1.0));
        }
    }

    [Test]
    public void DoesNotMoveThePositionWhenTheRateChanges()
    {
        PipelineTimeline timeline = new(() => { }, () => false);
        Assert.That(timeline.Anchor(0, TimeSpan.FromSeconds(5)), Is.True);
        timeline.StartOrPause(0, MediaEngineState.Playing);

        Thread.Sleep(50);
        TimeSpan before = timeline.Position;
        timeline.SetRate(4.0);
        TimeSpan after = timeline.Position;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.GreaterThanOrEqualTo(before), "retiming must not take the clock back");
            Assert.That(after - before, Is.LessThan(TimeSpan.FromSeconds(1)), "and must not jump it forward either");
        }
    }

    [Test]
    public void RunsAtTheRateItIsGiven()
    {
        PipelineTimeline timeline = new(() => { }, () => false);
        Assert.That(timeline.Anchor(0, TimeSpan.Zero), Is.True);
        timeline.StartOrPause(0, MediaEngineState.Playing);
        timeline.SetRate(4.0);

        TimeSpan start = timeline.Position;
        Thread.Sleep(RateWindowMilliseconds);
        TimeSpan advanced = timeline.Position - start;

        // Only a lower bound: a machine that stalls makes the clock advance further, never less, so this cannot fail for the
        // wrong reason - at one times it could not get this far in this wall time.
        Assert.That(advanced, Is.GreaterThan(TimeSpan.FromMilliseconds(2 * RateWindowMilliseconds)),
            "at four times, the clock has to advance faster than wall time");
    }
}
