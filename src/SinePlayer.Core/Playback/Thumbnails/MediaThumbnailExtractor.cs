// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Thumbnails;

public static class MediaThumbnailExtractor
{
    /// <summary>Where in the file the frame is taken from: far enough in to be past titles, short enough to be cheap.</summary>
    private const double SampleFraction = 0.10;

    /// <summary>Where the fraction stops growing, so a two hour film is sampled as near the start as a two minute one.</summary>
    private const int MaxSamplePositionSeconds = 10;

    /// <summary>
    /// Packets read while looking for the frame. A seek lands on the key frame before the target, so the packets between
    /// the two have to be decoded to reach it, and this is how long that stretch may be. A file with more of it than this
    /// is given the last picture decoded — or no picture at all when not one packet could be read.
    /// </summary>
    private const int MaxPacketCount = 240;

    /// <summary>
    /// The picture at <paramref name="position"/>, or 10% into the file when it is null, with its longest edge at most
    /// <paramref name="maxEdge"/> and its shape the video's own.
    /// </summary>
    public static OperationResult<MediaThumbnail> TryExtract(
        ISourceProviderFactory sourceFactory,
        IVideoDecoderFactory decoderFactory,
        MediaLocator locator,
        TimeSpan? position,
        int maxEdge)
    {
        OperationResultCreator<MediaThumbnail> result = new();

        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(decoderFactory);
        ArgumentNullException.ThrowIfNull(locator);

        if (maxEdge <= 0)
        {
            return result.Failure("A thumbnail needs a size to be made at.");
        }

        OperationResult opened = sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful || source is null)
        {
            return result.Failure($"Unable to open the media file: {opened}");
        }

        using (source)
        {
            return TryExtract(source, decoderFactory, position, maxEdge);
        }
    }

    private static OperationResult<MediaThumbnail> TryExtract(
        IMediaSource source,
        IVideoDecoderFactory decoderFactory,
        TimeSpan? position,
        int maxEdge)
    {
        OperationResultCreator<MediaThumbnail> result = new();

        MediaInfo info = source.Info;
        IReadOnlyList<VideoStreamInfo> videoStreams = info.VideoStreams;
        if (videoStreams.Count == 0)
        {
            return result.Failure("The file has no video stream.");
        }

        VideoStreamInfo stream = videoStreams[0];
        TimeSpan samplePosition = position ?? ResolveSamplePosition(info.Duration);

        // A position past the end is read as the end of the file, so that a caller asking for one still gets a poster.
        if (info.Duration > TimeSpan.Zero && samplePosition > info.Duration)
        {
            samplePosition = info.Duration;
        }

        if (source.CanSeek && samplePosition > TimeSpan.Zero)
        {
            OperationResult sought = source.Seek(samplePosition, stream.Index);
            if (!sought.IsSuccessful)
            {
                return result.Failure($"Unable to seek to the frame to draw: {sought}");
            }
        }

        OperationResult<IComponentCreator<IVideoDecoder>> created = decoderFactory.Create(
            source,
            stream,
            new DecoderOptions { HardwareDecoding = false });
        if (!created.IsSuccessful || created.Result is null)
        {
            return result.Failure($"Unable to create a decoder for the video stream: {created}");
        }

        OperationResult decoderCreated = created.Result.Create(out IVideoDecoder? decoder);
        if (!decoderCreated.IsSuccessful || decoder is null)
        {
            return result.Failure($"Unable to create a decoder for the video stream: {decoderCreated}");
        }

        using (decoder)
        {
            return TryDecodeFrame(source, decoder, stream, samplePosition, maxEdge);
        }
    }

    private static OperationResult<MediaThumbnail> TryDecodeFrame(
        IMediaSource source,
        IVideoDecoder decoder,
        VideoStreamInfo stream,
        TimeSpan samplePosition,
        int maxEdge)
    {
        OperationResultCreator<MediaThumbnail> result = new();

        // Kept as the picture to fall back on when the file ends before the target is reached.
        RawVideoFrame? lastPicture = null;
        bool restarted = false;
        try
        {
            for (int i = 0; i < MaxPacketCount; i++)
            {
                OperationResult read = source.TryReadPacket(out MediaPacket? packet);
                if (!read.IsSuccessful)
                {
                    return result.Failure($"Unable to read a packet of the video stream: {read}");
                }

                if (packet is null)
                {
                    // A seek can land past the last frame — a position at the very end, a duration that outlasts the
                    // frames — and then there is nothing after it to decode. The beginning of the file still has a
                    // picture, so it is read once from there before the file is called empty.
                    if (!restarted && samplePosition > TimeSpan.Zero)
                    {
                        restarted = true;
                        if (source.CanSeek && source.Seek(TimeSpan.Zero, stream.Index).IsSuccessful)
                        {
                            continue;
                        }
                    }

                    break;
                }

                using (packet)
                {
                    if (packet.StreamIndex != stream.Index)
                    {
                        continue;
                    }

                    OperationResult decoded = decoder.TryDecode(packet, out IReadOnlyList<RawVideoFrame> frames);
                    if (!decoded.IsSuccessful)
                    {
                        return result.Failure($"Unable to decode the video stream: {decoded}");
                    }

                    RawVideoFrame? picture = TakeFrame(frames, samplePosition, ref lastPicture);
                    if (picture is not null)
                    {
                        try
                        {
                            return Convert(picture, stream, maxEdge, result);
                        }
                        finally
                        {
                            picture.Dispose();
                        }
                    }
                }
            }

            // A decoder holds frames back while it reorders them, so the frame at the target can still be inside it.
            OperationResult drained = decoder.Drain(out IReadOnlyList<RawVideoFrame> held);
            if (!drained.IsSuccessful)
            {
                return result.Failure($"Unable to drain the video decoder: {drained}");
            }

            RawVideoFrame? heldPicture = TakeFrame(held, samplePosition, ref lastPicture);
            if (heldPicture is not null)
            {
                try
                {
                    return Convert(heldPicture, stream, maxEdge, result);
                }
                finally
                {
                    heldPicture.Dispose();
                }
            }

            // Nothing reached the target: the last frame decoded is nearer to it than no picture at all.
            return lastPicture is null
                ? result.Failure("No picture came out of the video stream.")
                : Convert(lastPicture, stream, maxEdge, result);
        }
        finally
        {
            lastPicture?.Dispose();
        }
    }

    /// <summary>
    /// The first of these frames at or after <paramref name="samplePosition"/>, which the caller takes over, or null
    /// when all of them come out ahead of it. Frames are owned by the caller, so the ones not chosen are disposed here,
    /// except for the last one passed over, which stays in <paramref name="lastPicture"/> as the picture to fall back on.
    /// </summary>
    private static RawVideoFrame? TakeFrame(IReadOnlyList<RawVideoFrame> frames, TimeSpan samplePosition, ref RawVideoFrame? lastPicture)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            RawVideoFrame frame = frames[i];
            if (frame.Timestamp >= samplePosition)
            {
                for (int later = i + 1; later < frames.Count; later++)
                {
                    frames[later].Dispose();
                }

                return frame;
            }

            lastPicture?.Dispose();
            lastPicture = frame;
        }

        return null;
    }

    private static OperationResult<MediaThumbnail> Convert(RawVideoFrame frame, VideoStreamInfo stream, int maxEdge, OperationResultCreator<MediaThumbnail> result)
    {
        MediaThumbnail? thumbnail = BgraFrameConverter.TryConvert(frame, maxEdge, stream.Orientation);
        return thumbnail is null
            ? result.Failure($"The frame is in a pixel format a thumbnail cannot be made from: {frame.Format}.")
            : result.Success(thumbnail);
    }

    private static TimeSpan ResolveSamplePosition(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        TimeSpan fraction = duration * SampleFraction;
        var cap = TimeSpan.FromSeconds(MaxSamplePositionSeconds);
        return fraction < cap ? fraction : cap;
    }
}
