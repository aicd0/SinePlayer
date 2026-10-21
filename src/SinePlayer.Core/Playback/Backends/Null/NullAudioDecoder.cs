// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Backends.Null;

/// <summary>
/// A decoder that decodes nothing and plays silence, for the same reason <see cref="NullVideoDecoder"/> exists.
/// <para>
/// Every packet becomes one block of silent samples at the packet's timestamp, as long as the packet lasts and in
/// the stream's own rate, channel count and format. That is enough to keep the audio chain buffering, submitting
/// and running dry exactly as it would — a real device is handed silence it can play, not a payload it cannot
/// read — with no codec involved.
/// </para>
/// </summary>
public sealed class NullAudioDecoder : IAudioDecoder
{
    private readonly AudioStreamInfo _stream;

    internal NullAudioDecoder(AudioStreamInfo stream)
    {
        _stream = stream;
    }

    public MediaStreamInfo Stream => _stream;

    public bool IsHardwareAccelerated => false;

    public AudioSampleFormat OutputFormat => _stream.SampleFormat;

    public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!NullPlanarAudioBuffer.TryGetBytesPerSample(_stream.SampleFormat, out _))
        {
            // Nothing to produce, and saying so is better than a block every consumer would have to guess about.
            buffers = [];
            return PlaybackOperationResult.Failure($"A null audio decoder has no samples for the {_stream.SampleFormat} format.");
        }

        int sampleCount = _stream.SampleRate > 0 ? (int)Math.Round(packet.Duration.TotalSeconds * _stream.SampleRate) : 0;
        if (sampleCount <= 0 || _stream.ChannelCount <= 0)
        {
            // A packet with no duration describes no sound, and the chain is better off with nothing than with a
            // block of no samples that every consumer would have to special-case.
            buffers = [];
            return PlaybackOperationResult.Success;
        }

        buffers =
        [
            new RawAudioBuffer
            {
                Timestamp = packet.Timestamp,
                SampleRate = _stream.SampleRate,
                ChannelCount = _stream.ChannelCount,
                ChannelLayout = _stream.ChannelLayout,
                SampleFormat = _stream.SampleFormat,
                SampleCount = sampleCount,
                Buffer = new NullPlanarAudioBuffer(_stream.SampleFormat, _stream.ChannelCount, sampleCount),
            },
        ];

        return PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
    {
        buffers = [];
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
