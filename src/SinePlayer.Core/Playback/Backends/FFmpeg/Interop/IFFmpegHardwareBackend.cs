// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;

namespace SinePlayer.Core.Playback.Backends.FFmpeg.Interop;

/// <summary>
/// One graphics API's half of hardware decoding: the device libavcodec decodes on, and every answer that only that
/// API can give. The decoder above asks this interface and never asks which API it is talking to, so another API is
/// another implementation plus a line in <see cref="FFmpegHardwareBackends"/> — and nothing in the decoder changes.
/// </summary>
internal interface IFFmpegHardwareBackend : IDisposable
{
    /// <summary>
    /// The pixel format libavcodec names this API's frames with, which is how a frame is recognized as one that has
    /// not been copied back to memory yet.
    /// </summary>
    int FramePixelFormat { get; }

    /// <summary>
    /// The layout this API's decoder will put a stream laid out like this in. It is knowable from the stream, which is
    /// what lets a layout nothing can draw be refused while another decoder can still be asked for.
    /// </summary>
    PixelFormat GetFrameLayout(PixelFormat streamFormat);

    /// <summary>
    /// Whether a freshly allocated codec context may decode on this backend's device. What the attachment holds is
    /// reached by callbacks the context owns, so <see cref="Detach"/> belongs to the end of that context's life rather
    /// than to the end of this call.
    /// </summary>
    OperationResult TryAttach(nint codecContext);

    /// <summary>
    /// Releases what the attachment holds. Called only once the codec context that holds the callbacks is gone, because
    /// what is released is what those callbacks reach.
    /// </summary>
    void Detach();
}
