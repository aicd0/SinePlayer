// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The one thing that makes two videos playable at once: every renderer gets a graphics device of its own. A
/// renderer draws through its device's <em>immediate context</em>, and a frame is a sequence of calls on it, so a
/// shared device lets one player's state changes land between the other's setup and its draw — a frame composited
/// into the wrong surface, intermittently.
/// <para>
/// This is pinned the way it breaks: a renderer takes its device with it when it is disposed, so a factory that
/// cached one device would hand a dead one to the next caller. Creating a second renderer after disposing the
/// first is therefore the whole test — it passes only while the device is per renderer.
/// </para>
/// </summary>
[TestFixture]
public class D3D11VideoRendererFactoryTests
{
    [Test]
    public void CreatesItsNextRendererAfterThePreviousOneWasLetGoOf()
    {
        D3D11VideoRendererFactory factory = new();

        PlaybackOperationResult offered = factory.TryCreate(out IComponentCreator<IVideoRenderer>? creator);
        if (!offered.IsSuccessful || creator is null)
        {
            Assert.Ignore($"This machine offers no Direct3D device: {offered.Message}");
            return;
        }

        PlaybackOperationResult first = creator.Create(out IVideoRenderer? renderer);
        if (!first.IsSuccessful || renderer is null)
        {
            Assert.Ignore($"This machine offers no Direct3D device: {first.Message}");
            return;
        }

        renderer.Dispose();

        PlaybackOperationResult offeredAgain = factory.TryCreate(out IComponentCreator<IVideoRenderer>? nextCreator);
        Assert.That(offeredAgain.IsSuccessful, Is.True, offeredAgain.Message);
        Assert.That(nextCreator, Is.Not.Null);

        PlaybackOperationResult second = nextCreator!.Create(out IVideoRenderer? next);
        try
        {
            Assert.That(second.IsSuccessful, Is.True, second.Message);
            Assert.That(next, Is.Not.Null);
        }
        finally
        {
            next?.Dispose();
        }
    }
}
