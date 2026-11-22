// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Hosting;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    /// <summary>
    /// Playing is what needs the finer timer, and nothing else does: the request goes in with the state that waits for
    /// frames and comes back out with the state that does not, whatever that state is.
    /// </summary>
    [Test]
    public void HoldsTheFinerTimerWhilePlayingAndGivesItBackWhenItStops()
    {
        string path = RawAudioVideoFixtureFile.Create();
        try
        {
            int before = TimerResolution.Holders;
            using PlaybackPipeline pipeline = CreatePipeline(new FakeVideoRenderer(), new FakeAudioRenderer { PlaysWhatItIsGiven = true });

            OperationResult opened = Open(pipeline, path);
            Assert.That(opened.IsSuccessful, Is.True, opened.Message);

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Assert.That(TimerResolution.Holders, Is.EqualTo(before + 1), "a playing pipeline waits for frames with millisecond timeouts");

            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
            Assert.That(TimerResolution.Holders, Is.EqualTo(before), "a paused pipeline waits for nothing");

            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
            Assert.That(TimerResolution.Holders, Is.EqualTo(before + 1), "and playing again asks for it again");

            Assert.That(Await(pipeline.Stop()).IsSuccessful, Is.True);
            Assert.That(TimerResolution.Holders, Is.EqualTo(before), "a stopped pipeline gives it back");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
