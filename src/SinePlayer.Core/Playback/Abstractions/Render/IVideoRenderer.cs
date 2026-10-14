// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;

namespace SinePlayer.Core.Playback.Abstractions.Render;

/// <summary>
/// Presents decoded video frames. A renderer is the sink of the video chain, so switching renderers
/// is a matter of handing the next frame to a different implementation.
/// </summary>
public interface IVideoRenderer : IDisposable
{
    string Name { get; }

    PlaybackResult Configure(VideoStreamInfo stream, IVideoRenderTarget target);

    /// <summary>Presents one frame. The caller keeps ownership of the frame.</summary>
    PlaybackResult Render(RawVideoFrame frame);
}
