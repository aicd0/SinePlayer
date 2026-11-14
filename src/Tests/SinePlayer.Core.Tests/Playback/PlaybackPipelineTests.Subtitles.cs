// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Hosting;

using Windows.UI;
using Windows.UI.Text;

namespace SinePlayer.Core.Tests.Playback;

public partial class PlaybackPipelineTests
{
    [Test]
    public void ShowsTheCueThatCoversThePositionAndTakesItDownWhenItEnds()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

        WaitFor(
            () => subtitleRenderer.Texts.Contains(SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.5))),
            "the cue of the position has to be drawn when its moment comes");

        WaitFor(() => subtitleRenderer.Texts.Length == 0, "the cue has to come down when it ends");
    }

    [Test]
    public void KeepsACueOnScreenWhenThePositionIsMovedIntoIt()
    {
        // The cue's own packet lies before where the seek lands, so a re-read never delivers it again. Throwing the
        // window away on a seek would therefore blank the text until the cue happened to end. The cue outlasts the seek
        // by far, so the position cannot leave it while the machine is busy: what is asserted is the seek, not the speed
        // of the machine the test runs on.
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(20),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(20))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

        string cue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.2));
        WaitFor(() => subtitleRenderer.Texts.Contains(cue), "the long cue to be up");
        WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(0.5), "playback to be under way");

        Assert.That(Await(pipeline.Seek(TimeSpan.FromSeconds(5), SeekMode.Precise)).IsSuccessful, Is.True);
        WaitFor(() => pipeline.Position > TimeSpan.FromSeconds(5.2), "the seek to have taken effect");

        Assert.That(subtitleRenderer.Texts, Does.Contain(cue), "a cue that still covers the position stays up across a seek");
    }

    [Test]
    public void SwapsTheSubtitleRendererWhileTheTrackKeepsPlaying()
    {
        // What the Lab's pickers do: change the registration, then reconcile. The track stays selected and the new
        // renderer takes over the drawing â€” the old one is let go of, and its last drawing is cleared first.
        // The cue outlasts the switch by far, so what the new renderer is asked for cannot be a screen the cue has
        // already left: whether a reconcile lands inside the cue's three seconds is a question about the machine's
        // load, not about the swap.
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(20),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(20))],
        };

        FakeSubtitleRenderer first = new();
        FakeSubtitleRenderer second = new();
        SwappableSubtitleRendererFactory renderers = new(first);
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, first, renderers);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => first.ShowCount > 1, "the first renderer to be drawing");

        renderers.Swap(second);
        Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

        WaitFor(() => second.ShowCount > 0, "the second renderer takes the drawing over");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Disposed, Is.True, "the renderer that was replaced is released");
            Assert.That(pipeline.SubtitleChains[0].SelectedStream, Is.Not.Null, "the track itself is untouched by a renderer switch");
        }

        // The switch clears the screen before it draws again, so the new renderer's first drawing may well be an empty
        // one â€” what is being asked is that the words reach it, not that they arrived in the same breath.
        WaitFor(() => second.Texts.Length > 0, "the words to be drawn by the new one");
    }

    /// <summary>
    /// A change with no length still has to be drawn. A karaoke syllable turning over is one instant â€” nothing covers it
    /// as a window, so a loop that only asks "is something animating right now" never hears about it, and the words sit
    /// still while the song moves on. The drawing has to follow the instant, not the cue's end.
    /// </summary>
    [Test]
    public void DrawsTheCueAgainWhenAnInstantInsideItGoesBy()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(6),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(6))],
        };

        FakeSubtitleRenderer renderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(
            script,
            renderer,
            subtitleDecoders: new InstantSubtitleDecoderFactory(TimeSpan.FromSeconds(1.5)));

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

        WaitFor(() => renderer.Texts.Length > 0, "the cue to be up");
        int drawn = renderer.ShowCount;

        // The instant is a second and a half in, and the cue runs for six: a drawing has to arrive long before the cue
        // ends, which is the only other thing that would ask for one.
        WaitFor(() => renderer.ShowCount > drawn, "the drawing the instant asks for", 2500);
        Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "the cue is still up while it is drawn again");
    }

    [Test]
    public void SwapsTheSubtitleDecoderAndRebuildsTheRendererWithIt()
    {
        // A cue's format comes from the decoder, so the pair is judged again: a renderer is never kept across a decoder
        // change, because the new decoder may produce something the old renderer cannot draw.
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer renderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, renderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => renderer.ShowCount > 1, "the renderer to be drawing");

        long drawn = renderer.ShowCount;
        pipeline.SubtitleChains[0].Components.ClearDecoders();
        pipeline.SubtitleChains[0].Components.Register(new NullSubtitleDecoderFactory());
        Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

        // The null decoder produces no cues, so the last thing drawn is an empty set: the words that were up have to go,
        // which is itself the proof that the chain was rebuilt rather than left as it was.
        WaitFor(() => renderer.Texts.Length == 0, "the rebuilt chain to take the old cue down");
        Assert.That(renderer.ShowCount, Is.GreaterThan(drawn), "the renderer was rebuilt and asked to draw again");
    }

    [Test]
    public void TakesDownWhatWasShownWhenTheTrackIsTurnedOff()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => subtitleRenderer.Texts.Length == 1, "the cue to be up");

        Assert.That(Select(pipeline, MediaStreamKind.Subtitle, null).IsSuccessful, Is.True);

        WaitFor(() => subtitleRenderer.Texts.Length == 0, "nothing may be left on the surface of a track that was turned off");
        Assert.That(pipeline.SubtitleChains[0].SelectedStream, Is.Null);
    }

    [Test]
    public void CarriesThePresentationWithEverythingItDraws()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        // Set before anything is drawn, so the very first call has to carry it: a renderer is never told twice.
        SubtitlePresentation presentation = new()
        {
            Order = 3,
            OffsetX = 0.25,
            OffsetY = -0.5,
            FontFamily = "Segoe UI",
            FontSize = 32.0,
            FontStyle = FontStyle.Italic,
            FontWeight = new FontWeight { Weight = 700 },
            FontColor = Color.FromArgb(255, 12, 34, 56),
            OutlineWidth = 3.0,
            OutlineColor = Color.FromArgb(255, 65, 43, 21),
            ShadowEnabled = true,
            ShadowColor = Color.FromArgb(128, 12, 34, 56),
            ShadowDistance = 0.12,
            Opacity = 0.75,
        };
        pipeline.SubtitleChains[0].Presentation = presentation;

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => subtitleRenderer.Texts.Length == 1, "the cue to be up");

        // The frame is the pipeline's own answer about where the picture is, so it is left out of what the setting carries.
        Assert.That(
            subtitleRenderer.Presentations.Select(drawn => drawn with { Frame = default }),
            Is.All.EqualTo(presentation),
            "every drawing carries both where the words go and how they look");
    }

    /// <summary>
    /// A script may name a font the machine does not have and the file carries instead, so the renderer is built with what
    /// the source it was selected from holds â€” not with whatever another source had.
    /// </summary>
    [Test]
    public void GivesTheRendererTheAttachmentsOfTheSourceTheTrackCameFrom()
    {
        MediaAttachment attachment = new()
        {
            Name = "fonts/Track.ttf",
            MimeType = "application/x-truetype-font",
            Data = new byte[] { 0x00, 0x01, 0x00, 0x00 },
        };

        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
            Attachments = [attachment],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);

        Assert.That(subtitleRenderer.ConfiguredAttachments, Has.Length.EqualTo(1), "the renderer is told what the file holds");
        Assert.That(subtitleRenderer.ConfiguredAttachments[0].Name, Is.EqualTo("fonts/Track.ttf"));
        Assert.That(subtitleRenderer.ConfiguredAttachments[0].Data.ToArray(), Is.EqualTo(attachment.Data.ToArray()));
    }

    /// <summary>
    /// A picture fitted into the surface does not fill it: the words belong to the picture, so the presentation says where
    /// the picture is and the renderer draws there. A 320Ã—180 picture in a 640Ã—480 surface is drawn 640Ã—360 with a bar of
    /// 60 pixels above and below it, which is a quarter of the height in total.
    /// </summary>
    [Test]
    public void LaysTheSubtitlesOutInThePictureRatherThanTheSurface()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => subtitleRenderer.Texts.Length == 1, "the cue to be up");

        SubtitleFrame frame = subtitleRenderer.Presentations[^1].Frame;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Left, Is.EqualTo(0.0).Within(0.001), "the picture is as wide as the surface");
            Assert.That(frame.Width, Is.EqualTo(1.0).Within(0.001));
            Assert.That(frame.Top, Is.EqualTo(0.125).Within(0.001), "and the bars above and below it are not its frame");
            Assert.That(frame.Height, Is.EqualTo(0.75).Within(0.001));
        }
    }

    /// <summary>
    /// The box a script is laid out in keeps the picture's own shape whatever the transform does to the picture, because
    /// libass scales a script onto that box one axis at a time: a box of the surface's shape draws every glyph stretched.
    /// The box's height is the picture's, and its width follows from the picture's shape, so a picture drawn as tall as the
    /// surface â€” filled, or stretched to a surface of another shape â€” gives a box wider than the surface, whose sides are
    /// cropped as the picture's are. Narrowing that box to the surface instead would make it shorter too, and would lift
    /// the words off the bottom of the picture.
    /// </summary>
    [Test]
    public void KeepsTheScriptInABoxOfThePicturesShape()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);

        IVideoChain video = pipeline.VideoChains[0];

        foreach (VideoResizeMode mode in (VideoResizeMode[])[VideoResizeMode.Fit, VideoResizeMode.Fill, VideoResizeMode.Stretch, VideoResizeMode.OriginalSize])
        {
            video.Transform = VideoTransform.Identity with { ResizeMode = mode };
            AssertPicturesShape(video.SubtitleFrame, $"{mode} keeps the picture's shape");
        }

        // Filled or stretched, the picture is drawn 426Ã—240 in this 320Ã—240 surface, and the box is that size too: the
        // words are cropped with the picture rather than shrunk to fit the surface's width, which would float them up.
        foreach (VideoResizeMode mode in (VideoResizeMode[])[VideoResizeMode.Fill, VideoResizeMode.Stretch])
        {
            video.Transform = VideoTransform.Identity with { ResizeMode = mode };
            SubtitleFrame filled = video.SubtitleFrame;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(filled.Width, Is.EqualTo(426.67 / 320.0).Within(0.001), $"{mode} keeps the box the picture's width");
                Assert.That(filled.Height, Is.EqualTo(1.0).Within(0.001), $"{mode} keeps it the picture's height");
                Assert.That(filled.Left, Is.EqualTo(-53.33 / 320.0).Within(0.001), "and it runs past both sides of the surface");
            }
        }

        // Zoomed out, the picture is smaller than the surface, and the words may not be larger than the picture they
        // belong to: half the size is the whole of it.
        video.Transform = VideoTransform.Identity with { Zoom = 0.5 };
        SubtitleFrame small = video.SubtitleFrame;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(small.Width, Is.EqualTo(0.5).Within(0.001), "the box is the picture's own size");
            Assert.That(small.Height, Is.EqualTo(0.375).Within(0.001));
            Assert.That(small.Left, Is.EqualTo(0.25).Within(0.001), "centred on the surface the picture sits on");
            Assert.That(small.Top, Is.EqualTo(0.3125).Within(0.001));
        }

        // Panned, the box follows the picture's centre: a quarter of the way to the right moves it by 40 of the 160 pixels
        // the picture is wide, which is an eighth of the surface.
        video.Transform = VideoTransform.Identity with { Zoom = 0.5, PanX = 0.25 };
        Assert.That(video.SubtitleFrame.Left, Is.EqualTo(0.375).Within(0.001), "the words move with the picture");

        // Panned off the surface, the picture the viewer ends up with is only the part still on it â€” 45 of the 90 pixels â€”
        // and the box is that tall, at the picture's shape: 80 pixels wide.
        video.Transform = VideoTransform.Identity with { Zoom = 0.5, PanY = 1.0 };
        SubtitleFrame cropped = video.SubtitleFrame;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cropped.Height, Is.EqualTo(45.0 / 240.0).Within(0.001), "as tall as what the surface leaves of the picture");
            Assert.That(cropped.Width, Is.EqualTo(80.0 / 320.0).Within(0.001), "and as wide as the picture's shape makes that");
        }
    }

    /// <summary>
    /// The picture is measured in the pixels the window draws it in, which are the stream's own: a window reports its size
    /// in device independent pixels, and on a display that is not at 100% those are fewer than the pixels drawn. A picture
    /// of the stream's own size â€” OriginalSize â€” is the mode where that difference shows, because it is the only one whose
    /// size does not come from the window; before this, a 320Ã—180 picture in a 320Ã—240 window at 2Ã— was measured as
    /// filling the window, and the words were laid out across it instead of around the picture.
    /// </summary>
    [Test]
    public void MeasuresThePictureInThePixelsTheWindowDrawsItIn()
    {
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreatePipeline(
            new FakeVideoRenderer(),
            new FakeAudioRenderer { PlaysWhatItIsGiven = true },
            videoDecoders: new SyntheticVideoDecoderFactory(),
            sourceFactory: new SyntheticSourceProviderFactory(script),
            audioDecoders: new SyntheticAudioDecoderFactory(),
            subtitleDecoders: new SyntheticSubtitleDecoderFactory(),
            subtitleRenderers: new FakeSubtitleRendererFactory(subtitleRenderer),
            videoTarget: new FakeRenderTarget { RasterizationScale = 2.0 },
            subtitleTarget: new SubtitleSurface());

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);

        // The window is 320Ã—240 device independent pixels, so it draws 640Ã—480. The picture at its own size is 320Ã—180 of
        // those, centred: an eighth of the surface above and below it, and a quarter of it either side.
        IVideoChain video = pipeline.VideoChains[0];
        video.Transform = VideoTransform.Identity with { ResizeMode = VideoResizeMode.OriginalSize };
        SubtitleFrame frame = video.SubtitleFrame;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frame.Left, Is.EqualTo(0.25).Within(0.001), "the picture starts a quarter of the way across");
            Assert.That(frame.Top, Is.EqualTo(0.3125).Within(0.001), "and an eighth of the way down");
            Assert.That(frame.Width, Is.EqualTo(0.5).Within(0.001), "it is half as wide as the window");
            Assert.That(frame.Height, Is.EqualTo(0.375).Within(0.001), "and three eighths as tall");
        }
    }

    /// <summary>
    /// The picture the words belong to is the host's to name, so a picture added after them is not adopted: the words keep
    /// being laid out against the whole surface until the host binds theirs. That is what makes a second view's words sit
    /// on its own picture â€” before this, the pipeline pointed every set of words at the first picture it had, which in a
    /// session with two views is another window's.
    /// </summary>
    [Test]
    public void LaysTheWordsOutInThePictureTheyWereBoundTo()
    {
        string playing = SyntheticPath("words-then-picture");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer words = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            // The words first, the picture second, and no reset in between: the picture is added while the words already
            // exist, which is the ordering a page that picks subtitles before a video track produces.
            ISubtitleChain subtitles = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));
            subtitles.Components.Register(new SyntheticSubtitleDecoderFactory());
            subtitles.Components.Register(new FakeSubtitleRendererFactory(words));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);

            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));

            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(subtitles.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => words.Texts.Length == 1, "the cue to be up");

            Assert.That(words.Presentations[^1].Frame.Height, Is.EqualTo(1.0).Within(0.001), "no picture was bound, so the words use the whole surface");

            // Binding is the host's call, and it takes effect without stopping anything.
            subtitles.LayoutVideoChain = video;
            WaitFor(() => words.Presentations[^1].Frame.Height < 0.99, "the words to be laid out in the bound picture");

            SubtitleFrame frame = words.Presentations[^1].Frame;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(frame.Top, Is.EqualTo(0.125).Within(0.001), "the words are laid out in the picture they were bound to");
                Assert.That(frame.Height, Is.EqualTo(0.75).Within(0.001));
            }
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A picture that leaves the pipeline takes nothing with it: the words it was measuring are let go and fall back to the
    /// whole surface, rather than being passed to whichever picture is left â€” another window's, in a session with two.
    /// </summary>
    [Test]
    public void LetsGoOfAPictureItWasLaidOutInWhenThatPictureIsRemoved()
    {
        string playing = SyntheticPath("words-picture-removed");
        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer words = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);

            IVideoChain video = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            video.Components.Register(new SyntheticVideoDecoderFactory());
            video.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));

            ISubtitleChain subtitles = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));
            subtitles.Components.Register(new SyntheticSubtitleDecoderFactory());
            subtitles.Components.Register(new FakeSubtitleRendererFactory(words));
            subtitles.LayoutVideoChain = video;

            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);
            Assert.That(Await(video.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(subtitles.SelectStream(pipeline.Sources[0].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => words.Texts.Length == 1, "the cue to be up");
            WaitFor(() => words.Presentations[^1].Frame.Height < 0.99, "the words to be laid out in the picture");

            Assert.That(Await(pipeline.RemoveChain(video)).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            WaitFor(() => words.Presentations[^1].Frame.Height > 0.99, "the words to fall back to the whole surface");
            Assert.That(words.Presentations[^1].Frame.Top, Is.EqualTo(0.0).Within(0.001));
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    [Test]
    public void DrawsAgainWhenOnlyTheFontChanges()
    {
        // The font travels with the drawing, so a change to it has to be drawn even though the cue, the position and the
        // surface are all the same. Without that a new size would only appear with the next cue.
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => subtitleRenderer.Texts.Length == 1, "the cue to be up");
        Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

        long drawn = subtitleRenderer.ShowCount;
        pipeline.SubtitleChains[0].Presentation = SubtitlePresentation.Identity with { FontFamily = "Segoe UI", FontSize = 32.0 };

        WaitFor(() => subtitleRenderer.ShowCount > drawn, "the new font to be drawn");
        Assert.That(subtitleRenderer.Presentations[^1].FontSize, Is.EqualTo(32.0), "and the drawing carries it");
    }

    [Test]
    public void DrawsNothingAgainWhileTheFileIsPaused()
    {
        // A paused file cannot change, so a pass that finds the same cues and the same presentation must not ask the
        // renderer to draw them again. (A loop that wakes without drawing is invisible from here â€” what a renderer does
        // besides draw is nothing â€” so this holds the redraw, which is the part a consumer can feel.)
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(script, subtitleRenderer);

        Assert.That(OpenForSubtitles(pipeline).IsSuccessful, Is.True);
        Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);
        WaitFor(() => subtitleRenderer.Texts.Length == 1, "the cue to be up");

        Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);

        // Let a pass or two of the loop go by: nothing changed, so nothing may be drawn.
        long drawn = subtitleRenderer.ShowCount;
        Thread.Sleep(300);

        Assert.That(subtitleRenderer.ShowCount, Is.EqualTo(drawn), "a paused subtitle loop redraws nothing");
    }

    [Test]
    public void RefusesToBuildASubtitleChainTheRendererCannotDraw()
    {
        // The format comes from the decoder, and a factory that cannot draw it refuses to offer a renderer at all â€” so
        // an incompatible pair is never assembled, which is the whole of the compatibility rule for subtitles.
        SyntheticMediaScript script = new()
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1))],
        };

        FakeSubtitleRenderer subtitleRenderer = new();
        using PlaybackPipeline pipeline = CreateSubtitlePipeline(
            script,
            subtitleRenderer,
            new RefusingSubtitleRendererFactory());

        OperationResult opened = OpenForSubtitles(pipeline, selectTheTrack: false);
        Assert.That(opened.IsSuccessful, Is.True, opened.Message);

        MediaInfo info = pipeline.Sources[0].Info;
        OperationResult selected = Select(pipeline, MediaStreamKind.Subtitle, info.SubtitleStreams[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.IsSuccessful, Is.False);
            Assert.That(pipeline.SubtitleChains[0].SelectedStream, Is.Not.Null, "the choice is recorded even though no pair could be built for it");
            Assert.That(subtitleRenderer.ShowCount, Is.Zero, "and nothing was drawn");
        }
    }

    /// <summary>
    /// A file added after the one being played contributes tracks of its own: its subtitle stream can be selected, and
    /// the cue it carries is drawn over the picture of the file that is playing. The file that was added first is what
    /// the timeline and the container facts are still read from.
    /// </summary>
    [Test]
    public void PlaysTheSubtitleTrackOfAFileAddedAfterTheOneBeingPlayed()
    {
        string playing = SyntheticPath("playing");
        string subtitles = SyntheticPath("subtitles");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(playing, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(3) });
        sources.Add(subtitles, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(3),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(3))],
        });

        FakeSubtitleRenderer subtitleRenderer = new();
        PlaybackPipeline pipeline = CreateMultiSourcePipeline(sources, subtitleRenderer);
        try
        {
            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(playing))).IsSuccessful, Is.True);

            MediaInfo info = pipeline.Sources[0].Info;
            Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);

            OperationResult<SubtitleStreamInfo> added = AddSubtitleFile(pipeline, subtitles);
            Assert.That(added.IsSuccessful, Is.True, added.Message);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.Sources, Has.Count.EqualTo(2), "the added file is on record");
                Assert.That(pipeline.Sources[1].Locator.Value, Is.EqualTo(subtitles));
                Assert.That(pipeline.Sources[1].Info.Streams, Does.Contain(added.Result), "the track handed back is one of the file's own");
                Assert.That(pipeline.Sources[0].Info, Is.SameAs(info), "the file that was added first is the one whose container is reported");
            }

            Assert.That(Select(pipeline, MediaStreamKind.Subtitle, added.Result).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            string cue = SyntheticSubtitleDecoderFactory.TextAt(TimeSpan.FromSeconds(0.2));
            WaitFor(() => subtitleRenderer.Texts.Contains(cue), "the cue of the added file to be drawn");
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A picture can be taken out while its words stay. They were laid out against it, so they are laid out against the
    /// picture that is left â€” and they keep being drawn, which is what says the removal did not take them with it.
    /// </summary>
    [Test]
    public void LaysTheWordsOutAgainstTheNextPictureWhenTheirsIsRemoved()
    {
        int baseline = PipelineThread.LiveCount;

        string firstPosition = SyntheticPath("leaving-picture");
        string secondPosition = SyntheticPath("staying-picture");
        string wordsPosition = SyntheticPath("words-for-both");

        SyntheticMultiSourceProviderFactory sources = new();
        sources.Add(firstPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });
        sources.Add(secondPosition, new SyntheticMediaScript { Duration = TimeSpan.FromSeconds(10) });
        sources.Add(wordsPosition, new SyntheticMediaScript
        {
            Duration = TimeSpan.FromSeconds(10),
            Subtitles = [new ScriptedSubtitle(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(10))],
        });

        FakeVideoRenderer leavingRenderer = new();
        FakeSubtitleRenderer words = new();
        PlaybackPipeline pipeline = new() { SourceFactory = sources };
        try
        {
            IVideoChain leaving = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            IVideoChain staying = Await(pipeline.AddVideoChain(new FakeRenderTarget()));
            ISubtitleChain subtitles = Await(pipeline.AddSubtitleChain(new SubtitleSurface()));

            leaving.Components.Register(new SyntheticVideoDecoderFactory());
            leaving.Components.Register(new FakeVideoRendererFactory(leavingRenderer));
            staying.Components.Register(new SyntheticVideoDecoderFactory());
            staying.Components.Register(new FakeVideoRendererFactory(new FakeVideoRenderer()));
            subtitles.Components.Register(new SyntheticSubtitleDecoderFactory());
            subtitles.Components.Register(new FakeSubtitleRendererFactory(words));

            Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(firstPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(secondPosition))).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(wordsPosition))).IsSuccessful, Is.True);

            Assert.That(Await(leaving.SelectStream(pipeline.Sources[0].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(staying.SelectStream(pipeline.Sources[1].Info.VideoStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(subtitles.SelectStream(pipeline.Sources[2].Info.SubtitleStreams[0])).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

            WaitFor(() => words.ShowCount > 0, "the words to be drawn");

            // The reader, two for each picture and one for the words.
            WaitFor(() => PipelineThread.LiveCount - baseline == 6, $"all six threads to be running (was {PipelineThread.LiveCount - baseline})");

            Assert.That(Await(pipeline.RemoveChain(leaving)).IsSuccessful, Is.True);
            Assert.That(Await(pipeline.Reconcile()).IsSuccessful, Is.True);

            long drawn = words.ShowCount;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pipeline.VideoChains, Is.EqualTo(new[] { staying }), "only the picture that was asked to go has gone");
                Assert.That(pipeline.State, Is.EqualTo(MediaEngineState.Playing), "and playback never left");
            }

            WaitFor(() => PipelineThread.LiveCount - baseline == 4, $"the picture that went to cost no thread (was {PipelineThread.LiveCount - baseline})");
            WaitFor(() => words.ShowCount > drawn, "the words to keep being drawn against the picture that is left");
            Assert.That(Await(pipeline.Pause()).IsSuccessful, Is.True);
        }
        finally
        {
            sources.Release();
            pipeline.Dispose();
        }
    }

    /// <summary>
    /// A subtitle file given to a real playback is read by the backend the picture is read by: the events it holds become
    /// cues drawn over the file that is playing, without that file being reopened or its tracks touched.
    /// </summary>
    [Test]
    public void PlaysTheCuesOfASubtitleFileAddedToARealPlayback()
    {
        string path = RawAudioVideoFixtureFile.CreateMultiTrack();
        string subtitlePath = Path.Combine(Path.GetTempPath(), "sineplayer-external-subtitle.srt");

        try
        {
            File.WriteAllText(subtitlePath, "1\r\n00:00:00,200 --> 00:00:02,000\r\nHello from a file of its own\r\n\r\n");

            FakeSubtitleRenderer subtitleRenderer = new();
            using (PlaybackPipeline pipeline = CreatePipeline(
                new FakeVideoRenderer(),
                new FakeAudioRenderer { PlaysWhatItIsGiven = true },
                subtitleDecoders: new FFmpegSubtitleDecoderFactory(),
                subtitleRenderers: new FakeSubtitleRendererFactory(subtitleRenderer)))
            {
                Assert.That(Await(pipeline.Reset()).IsSuccessful, Is.True);
                Assert.That(Await(pipeline.AddSource(MediaLocator.Parse(path))).IsSuccessful, Is.True);

                MediaInfo info = pipeline.Sources[0].Info;
                Assert.That(Select(pipeline, MediaStreamKind.Video, info.VideoStreams[0]).IsSuccessful, Is.True);

                OperationResult<SubtitleStreamInfo> added = AddSubtitleFile(pipeline, subtitlePath);
                Assert.That(added.IsSuccessful, Is.True, added.Message);
                Assert.That(Select(pipeline, MediaStreamKind.Subtitle, added.Result).IsSuccessful, Is.True);
                Assert.That(Await(pipeline.Play()).IsSuccessful, Is.True);

                WaitFor(
                    () => subtitleRenderer.Texts.Any(text => text.Contains("Hello from a file of its own", StringComparison.Ordinal)),
                    "the cue of the subtitle file to be drawn over the picture");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(subtitlePath);
        }
    }

}
