// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Tests.Playback;

/// <summary>
/// Media the pipeline is driven with by hand: a source that serves packets from a script, and decoders that turn one
/// packet into one frame or one block of samples.
/// <para>
/// It exists for the shapes a real file cannot be cut into. Every finite file this project can mux reports the end
/// when it is read out, and the reader reaches that end long before playback needs it — so a stream that stops
/// arriving while the rest of the file flows cannot be told from one that has ended by anything but the reader's own
/// account of what it has read (§8). A live source is the shape where that account is all there is: it never
/// reports an end of file at all.
/// </para>
/// </summary>
internal sealed class SyntheticMediaScript
{
    /// <summary>How much media the file holds, in both streams.</summary>
    internal TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Video packets are withheld from here on, as a source that has stopped carrying the stream is.</summary>
    internal TimeSpan VideoStopsAt { get; init; } = TimeSpan.MaxValue;

    /// <summary>Audio packets are withheld from here on.</summary>
    internal TimeSpan AudioStopsAt { get; init; } = TimeSpan.MaxValue;

    /// <summary>
    /// Whether reading past the last packet reports the end of the file. A live source does not: it waits for what
    /// comes next, which is the state in which no end of file can answer for a stream that has gone quiet.
    /// </summary>
    internal bool ReportsTheEndOfTheFile { get; init; } = true;

    internal int FrameRate { get; init; } = 30;

    internal int SampleRate { get; init; } = 44100;

    internal int SamplesPerAudioPacket { get; init; } = 1024;
}

internal sealed class SyntheticSourceProviderFactory : ISourceProviderFactory
{
    private readonly SyntheticMediaScript _script;
    private SyntheticMediaSource? _opened;

    internal SyntheticSourceProviderFactory(SyntheticMediaScript script)
    {
        _script = script;
    }

    public ComponentCapabilities Capabilities { get; } = new() { Name = "synthetic" };

    public PlaybackOperationResult TryOpen(MediaLocator locator, out IMediaSource? source)
    {
        _opened = new SyntheticMediaSource(_script);
        source = _opened;
        return PlaybackOperationResult.Success;
    }

    /// <summary>
    /// Lets a source that is waiting for a live stream stop waiting. The reader's wait inside the file is the one
    /// wait the pipeline does not own — it ends when the file does — so a test that stops a live source before the
    /// pipeline must say so itself, or the teardown waits for a packet that is never coming.
    /// </summary>
    internal void Release()
    {
        _opened?.Release();
    }
}

internal sealed class SyntheticMediaSource : IMediaSource
{
    private readonly SyntheticMediaScript _script;
    private readonly ManualResetEventSlim _closed = new(false);
    private readonly List<ScriptedPacket> _timeline = [];

    private int _next;

    internal SyntheticMediaSource(SyntheticMediaScript script)
    {
        _script = script;

        VideoStreamInfo video = new()
        {
            Index = 0,
            CodecId = "synthetic-video",
            Width = 320,
            Height = 180,
            FrameRate = script.FrameRate,
            PixelFormat = PixelFormat.Yuv420P,
        };

        AudioStreamInfo audio = new()
        {
            Index = 1,
            CodecId = "synthetic-audio",
            SampleRate = script.SampleRate,
            ChannelCount = 2,
            ChannelLayout = ChannelLayout.Stereo,
            SampleFormat = AudioSampleFormat.Int16,
        };

        Info = new MediaInfo
        {
            ContainerId = "synthetic",
            Duration = script.Duration,
            Streams = [video, audio],
        };

        int frameCount = (int)(script.Duration.TotalSeconds * script.FrameRate);
        for (int index = 0; index < frameCount; index++)
        {
            _timeline.Add(new ScriptedPacket(true, TimeSpan.FromSeconds((double)index / script.FrameRate), TimeSpan.FromSeconds(1.0 / script.FrameRate)));
        }

        int packetCount = (int)(script.Duration.TotalSeconds * script.SampleRate / script.SamplesPerAudioPacket);
        for (int index = 0; index < packetCount; index++)
        {
            var start = TimeSpan.FromSeconds((double)(index * script.SamplesPerAudioPacket) / script.SampleRate);
            var length = TimeSpan.FromSeconds((double)script.SamplesPerAudioPacket / script.SampleRate);
            _timeline.Add(new ScriptedPacket(false, start, length));
        }

        // Interleaved the way a real file is, so the reader meets the two streams together.
        _timeline.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
    }

    public MediaInfo Info { get; }

    public bool CanSeek => false;

    public PlaybackOperationResult Seek(TimeSpan position)
    {
        // Nothing here is seekable, and no test that uses it seeks: the reader's first look asks for zero, which is
        // where the timeline already is.
        _next = 0;
        return PlaybackOperationResult.Success;
    }

    public PlaybackOperationResult TryReadPacket(out MediaPacket? packet)
    {
        while (true)
        {
            if (_closed.IsSet)
            {
                packet = null;
                return PlaybackOperationResult.Success;
            }

            if (_next >= _timeline.Count)
            {
                if (_script.ReportsTheEndOfTheFile)
                {
                    packet = null;
                    return PlaybackOperationResult.Success;
                }

                // A live source has no end to report: it waits for what comes next.
                _closed.Wait(20);
                continue;
            }

            ScriptedPacket scripted = _timeline[_next++];
            if (scripted.Video ? scripted.Timestamp >= _script.VideoStopsAt : scripted.Timestamp >= _script.AudioStopsAt)
            {
                continue;
            }

            packet = new MediaPacket
            {
                StreamIndex = scripted.Video ? 0 : 1,
                Timestamp = scripted.Timestamp,
                Duration = scripted.Length,
                IsKeyFrame = true,
                Buffer = new SyntheticMediaBuffer(),
            };

            return PlaybackOperationResult.Success;
        }
    }

    /// <summary>Lets the wait for a packet that is never coming end, so the reader can be joined.</summary>
    internal void Release()
    {
        _closed.Set();
    }

    public void Dispose()
    {
        _closed.Set();
        _closed.Dispose();
    }

    private readonly record struct ScriptedPacket(bool Video, TimeSpan Timestamp, TimeSpan Length);
}

internal sealed class SyntheticMediaBuffer : IMediaBuffer
{
    public long Length => 0;

    public void Dispose()
    {
    }
}

internal sealed class SyntheticVideoDecoderFactory : IVideoDecoderFactory
{
    public ComponentCapabilities Capabilities { get; } = new() { Name = "synthetic video" };

    public PlaybackOperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IVideoDecoder? decoder)
    {
        decoder = new Decoder(stream);
        return PlaybackOperationResult.Success;
    }

    private sealed class Decoder(VideoStreamInfo stream) : IVideoDecoder
    {
        public MediaStreamInfo Stream => stream;

        public bool IsHardwareAccelerated => false;

        public PixelFormat OutputFormat => PixelFormat.Yuv420P;

        public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
        {
            frames =
            [
                new RawVideoFrame
                {
                    Timestamp = packet.Timestamp,
                    Duration = packet.Duration,
                    Width = stream.Width,
                    Height = stream.Height,
                    Format = PixelFormat.Yuv420P,
                    Buffer = new SyntheticMediaBuffer(),
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
}

internal sealed class SyntheticAudioDecoderFactory : IAudioDecoderFactory
{
    public ComponentCapabilities Capabilities { get; } = new() { Name = "synthetic audio" };

    public PlaybackOperationResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IAudioDecoder? decoder)
    {
        decoder = new Decoder(stream);
        return PlaybackOperationResult.Success;
    }

    private sealed class Decoder(AudioStreamInfo stream) : IAudioDecoder
    {
        public MediaStreamInfo Stream => stream;

        public bool IsHardwareAccelerated => false;

        public AudioSampleFormat OutputFormat => AudioSampleFormat.Int16;

        public PlaybackOperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
        {
            buffers =
            [
                new RawAudioBuffer
                {
                    Timestamp = packet.Timestamp,
                    SampleRate = stream.SampleRate,
                    ChannelCount = stream.ChannelCount,
                    ChannelLayout = stream.ChannelLayout,
                    SampleFormat = AudioSampleFormat.Int16,
                    SampleCount = (int)(packet.Duration.TotalSeconds * stream.SampleRate),
                    Buffer = new SyntheticMediaBuffer(),
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
}
