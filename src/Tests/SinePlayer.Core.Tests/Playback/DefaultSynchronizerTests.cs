// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Timing;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The synchronizer only classifies a frame against the clock, so its whole behaviour is a table of
/// offsets — which is exactly why it is kept swappable and verified here rather than through playback.
/// </summary>
[TestFixture]
public class DefaultSynchronizerTests
{
    [Test]
    public void PresentsFramesThatLineUp()
    {
        DefaultSynchronizer synchronizer = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronizer.Evaluate(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Present));
            Assert.That(synchronizer.Evaluate(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Present));
            Assert.That(synchronizer.Evaluate(TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(39), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Present));
        }
    }

    [Test]
    public void WaitsForFramesThatAreEarly()
    {
        DefaultSynchronizer synchronizer = new();

        Assert.That(synchronizer.Evaluate(TimeSpan.FromMilliseconds(1200), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Wait));
    }

    [Test]
    public void DropsFramesThatAreTooLate()
    {
        DefaultSynchronizer synchronizer = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronizer.Evaluate(TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Drop));
            Assert.That(synchronizer.Evaluate(TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(41), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Drop));
        }
    }

    [Test]
    public void KeepsTheTolerancesItWasGiven()
    {
        DefaultSynchronizer synchronizer = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronizer.Evaluate(TimeSpan.FromMilliseconds(1040), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Present));
            Assert.That(synchronizer.Evaluate(TimeSpan.FromMilliseconds(1060), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Wait));
            Assert.That(synchronizer.Evaluate(TimeSpan.FromMilliseconds(940), TimeSpan.FromSeconds(1)), Is.EqualTo(FrameSyncAction.Drop));
        }
    }
}
