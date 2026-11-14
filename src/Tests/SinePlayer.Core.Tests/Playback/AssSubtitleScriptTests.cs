// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Backends.Libass;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The script libass is handed: what a cue becomes once its timing is taken from the cue rather than from the decoder's
/// line, and what a stream without a header is drawn with. Getting this wrong is not a drawing bug — a line libass cannot
/// parse simply never appears.
/// </summary>
[TestFixture]
public class AssSubtitleScriptTests
{
    [Test]
    public void PutsTheCuesOwnTimesWhereALineExpectsThem()
    {
        SubtitleCue cue = new()
        {
            Start = TimeSpan.FromSeconds(65),
            End = TimeSpan.FromSeconds(67.5),
            Text = @"2,Sign,,10,20,30,{\pos(1,2)}Hello, world",
        };

        string script = AssSubtitleScript.Compose(null, new[] { cue });

        Assert.That(
            script,
            Does.Contain(@"Dialogue: 2,0:01:05.00,0:01:07.50,Sign,,10,20,30,{\pos(1,2)}Hello, world"),
            "the cue's own times are used in the two fields the line does not carry, and the text keeps its commas and tags");
    }

    [Test]
    public void GivesAStreamWithoutAHeaderAStyleToDraw()
    {
        SubtitleCue cue = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(1),
            Text = "0,Default,,0,0,0,,Hello",
        };

        string script = AssSubtitleScript.Compose(null, new[] { cue });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(script, Does.Contain("[V4+ Styles]"), "libass cannot draw a cue that names a style it was never given");
            Assert.That(script, Does.Contain("Style: Default,Arial"), "and the default style has to be the one the cue names");
            Assert.That(script, Does.StartWith("[Script Info]"));
        }
    }

    [Test]
    public void KeepsTheStreamsOwnHeader()
    {
        const string header = "[Script Info]\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nStyle: Top,Arial,40\n";

        string script = AssSubtitleScript.Compose(header, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(script, Does.StartWith("[Script Info]\nPlayResX: 1920"), "the stream's own resolution wins over the default");
            Assert.That(script, Does.Not.Contain("Style: Default,Arial,20"), "and its styles are not followed by ours");
        }
    }

    [Test]
    public void LeavesOutCuesThatCarryNoText()
    {
        SubtitleCue bitmap = new()
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(1),
        };

        string script = AssSubtitleScript.Compose(null, new[] { bitmap });

        Assert.That(script, Does.Not.Contain("Dialogue:"), "a picture is not this renderer's work, and an empty line is not a cue");
    }

    [Test]
    public void WritesAScriptTimeInHundredths()
    {
        SubtitleCue cue = new()
        {
            Start = new TimeSpan(0, 1, 2, 3, 459),
            End = new TimeSpan(0, 1, 2, 4, 999),
            Text = "0,Default,,0,0,0,,Hi",
        };

        string script = AssSubtitleScript.Compose(null, new[] { cue });

        Assert.That(script, Does.Contain("Dialogue: 0,1:02:03.45,1:02:04.99,"), "the format carries hundredths and truncates the rest");
    }
}
