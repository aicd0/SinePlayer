// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

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

    PlaybackOperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target);

    /// <summary>Presents one frame. The caller keeps ownership of the frame.</summary>
    PlaybackOperationResult Render(RawVideoFrame frame);

    /// <summary>
    /// Takes up a change the host has reported to the presentation surface — a new size, or a new display
    /// scale — and draws the last frame again, so the picture follows the surface instead of being
    /// stretched into it by the host.
    /// <para>
    /// This is the paused case: while frames are being presented, <see cref="Render"/> applies the change
    /// on the next frame anyway, and nothing calls this. It has to be safe to call at any time and do
    /// nothing when there is nothing to take up, because whoever calls it while paused has no way to know
    /// whether the surface moved — the renderer is the one listening to it.
    /// </para>
    /// </summary>
    PlaybackOperationResult RefreshSurface();

    /// <summary>
    /// How many of the frames this renderer handed over actually reached the display, when the backend can
    /// tell — false when it cannot, which is the honest answer rather than zero.
    /// <para>
    /// The frames handed over are counted by the caller, so the difference is what the compositor discarded:
    /// a frame presented but superseded by a newer one before the display took it. It is a diagnostic, so it
    /// is allowed to lag by the frame or two that are still in flight, and to stop advancing on a system that
    /// will not report.
    /// </para>
    /// </summary>
    bool TryGetDisplayedFrameCount(out long count);
}
