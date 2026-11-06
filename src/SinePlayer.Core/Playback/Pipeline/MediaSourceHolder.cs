// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

internal sealed partial class MediaSourceHolder(IReadOnlyList<MediaSource> sources) : IDisposable
{
    private static MediaSource? FindSource(IEnumerable<MediaSource> sources, MediaStreamInfo stream)
    {
        foreach (MediaSource source in sources)
        {
            foreach (MediaStreamInfo candidate in source.Info.Streams)
            {
                if (candidate == stream)
                {
                    return source;
                }
            }
        }

        return null;
    }

    public IReadOnlyList<MediaSource> Sources { get; } = sources;

    public MediaSource? TryGetSourceOf(MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }

        return FindSource(Sources, stream);
    }

    public void Dispose()
    {
    }
}
