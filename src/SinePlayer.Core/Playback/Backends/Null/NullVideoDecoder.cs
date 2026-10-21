// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A decoder that decodes nothing and shows black.
/// <para>
/// Every packet becomes one black frame at the packet's own timestamp and duration. That is enough to keep the
/// video chain moving at the right pace — the frame is timed, queued and presented like any other, and a real
/// renderer can upload it, because it has readable planes — without a codec, a file or a GPU. It is what makes
/// the parts of the pipeline that have nothing to do with decoding testable: switching a decoder or a renderer
/// while playback runs, and watching the swap from the outside.
/// </para>
/// <para>
/// What it is to the pipeline — whether it is the decoder that is already running — is the factory's to say, in
/// the creator it offers: two of these can be registered at once and told apart there.
/// </para>
/// </summary>
public sealed class NullVideoDecoder : IVideoDecoder
{
    private readonly VideoStreamInfo _stream;

    internal NullVideoDecoder(VideoStreamInfo stream)
    {
        _stream = stream;
    }

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    /// <summary>What it produces, which is not the stream's format: the frame is a fixed black picture.</summary>
    public PixelFormat OutputFormat => PixelFormat.Yuv420P;

    public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(packet);

        frames =
        [
            new RawVideoFrame
            {
                Timestamp = packet.Timestamp,
                Duration = packet.Duration,
                Width = NullPlanarVideoBuffer.Width,
                Height = NullPlanarVideoBuffer.Height,
                Format = PixelFormat.Yuv420P,
                Buffer = new NullPlanarVideoBuffer(),
            },
        ];

        return PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
    {
        frames = [];
        return PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult Flush()
    {
        return PlaybackOperationResult.Success;
    }

    public void Dispose()
    {
    }
}
