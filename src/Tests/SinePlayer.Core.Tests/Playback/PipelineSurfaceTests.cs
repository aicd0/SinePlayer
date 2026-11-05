// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// A chain is the pipeline's own business. The day one of them is public, its per-stream shape is part of the
/// API, and a second chain of a kind would have to be expressed there as well. The scan follows
/// <see cref="IChainFeed"/>, which is what the reader feeds, so a chain introduced later is covered without
/// this test having to name it.
/// </summary>
[TestFixture]
public class PipelineSurfaceTests
{
    [Test]
    public void KeepsEveryChainInternal()
    {
        Type[] chains = [.. typeof(PlaybackPipeline).Assembly
            .GetTypes()
            .Where(type => type is { IsInterface: false } && typeof(IChainFeed).IsAssignableFrom(type))];
        Type[] guarded = [.. chains, typeof(IChainFeed), typeof(IPictureFrameSource)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chains, Is.Not.Empty, $"No chain implements {nameof(IChainFeed)} any more, so nothing was guarded.");
            foreach (Type type in guarded)
            {
                Assert.That(type.IsVisible, Is.False, $"{type.Name} is part of a chain and is visible outside the assembly.");
            }
        }
    }
}
