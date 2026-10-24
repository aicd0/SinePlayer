// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

public sealed partial class NullVideoDecoder : IVideoDecoder
{
    private readonly VideoStreamInfo _stream;

    internal NullVideoDecoder(VideoStreamInfo stream)
    {
        _stream = stream;
    }

    public string Name => "Null";

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    public PixelFormat OutputFormat => PixelFormat.Yuv420P;

    public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
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

        return OperationResult.Success();
    }

    public OperationResult Drain(out IReadOnlyList<RawVideoFrame> frames)
    {
        frames = [];
        return OperationResult.Success();
    }

    public OperationResult Flush()
    {
        return OperationResult.Success();
    }

    public void Dispose()
    {
    }
}
