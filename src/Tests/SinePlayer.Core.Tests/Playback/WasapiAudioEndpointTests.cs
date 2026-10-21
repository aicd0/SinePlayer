// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using NUnit.Framework;

using SinePlayer.Core.Playback.Backends.Wasapi;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// The output devices a machine offers, which is what a picker lists. This machine may have none — a build
/// agent has no sound card — so the call is what is pinned here and the devices themselves are reported.
/// </summary>
[TestFixture]
internal sealed class WasapiAudioEndpointTests
{
    [Test]
    public void ListsTheOutputDevicesThisMachineHas()
    {
        bool listed = WasapiAudioEndpoints.TryGetRenderEndpoints(out IReadOnlyList<WasapiAudioEndpoint> endpoints, out string error);
        Assert.That(listed, Is.True, error);

        int defaults = 0;
        foreach (WasapiAudioEndpoint endpoint in endpoints)
        {
            TestContext.Out.WriteLine($"{(endpoint.IsDefault ? "*" : " ")} {endpoint.Name} [{endpoint.Id}]");

            // What a picker shows, and what choosing it hands back: an id to open, a name to read.
            Assert.That(endpoint.Id, Is.Not.Empty);
            Assert.That(endpoint.Name, Is.Not.Empty);

            if (endpoint.IsDefault)
            {
                defaults++;
            }
        }

        Assert.That(defaults, Is.LessThanOrEqualTo(1), "Windows has at most one default output device");

        if (defaults == 1)
        {
            Assert.That(endpoints[0].IsDefault, Is.True, "the default device has to come first in the list");
        }

        if (endpoints.Count == 0)
        {
            Assert.Ignore("This machine reports no audio output device, so there is nothing to list.");
        }
    }
}
