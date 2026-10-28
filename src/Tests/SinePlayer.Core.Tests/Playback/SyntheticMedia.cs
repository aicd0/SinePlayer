// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
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

    /// <summary>
    /// How long the picture's stream declares itself to be, when it is shorter than the file it is in. Unset means
    /// the file's own length, which is the ordinary case of two streams of the same size.
    /// </summary>
    internal TimeSpan? VideoDuration { get; init; }

    /// <summary>How long the sound's stream declares itself to be. Unset means the file's own length.</summary>
    internal TimeSpan? AudioDuration { get; init; }

    /// <summary>
    /// Whether a seek lands on the keyframe at or before the request rather than on the first packet at or after it,
    /// which is what a demuxer does — and it lands by the picture's keyframes, since they are the only ones a
    /// container has. So a picture that ends before the request leaves the landing at its last keyframe, however far
    /// behind that is.
    /// </summary>
    internal bool LandsOnTheKeyframeBefore { get; init; }

    internal int FrameRate { get; init; } = 30;

    internal int SampleRate { get; init; } = 44100;

    /// <summary>How many channels the sound's stream declares, which is what a device with fewer speaks to.</summary>
    internal int AudioChannelCount { get; init; } = 2;

    internal ChannelLayout AudioChannelLayout { get; init; } = ChannelLayout.Stereo;

    internal int SamplesPerAudioPacket { get; init; } = 1024;

    /// <summary>
    /// Subtitle events, as a real container carries them: one packet per event, timed by the event itself. Empty means
    /// the file has no subtitle track at all, which is every script that does not ask for one.
    /// </summary>
    internal IReadOnlyList<ScriptedSubtitle> Subtitles { get; init; } = [];
}

/// <summary>One subtitle event of a scripted file.</summary>
internal readonly record struct ScriptedSubtitle(TimeSpan Start, TimeSpan End)
{
    internal TimeSpan Length => End - Start;
}

internal sealed class SyntheticSourceProviderFactory : ISourceProviderFactory
{
    private readonly SyntheticMediaScript _script;
    private SyntheticMediaSource? _opened;

    internal SyntheticSourceProviderFactory(SyntheticMediaScript script)
    {
        _script = script;
    }

    public OperationResult TryOpen(MediaLocator locator, out IMediaSource? source)
    {
        _opened = new SyntheticMediaSource(_script);
        source = _opened;
        return OperationResult.Success();
    }

    /// <summary>The source this factory handed out, for a test that wants to see what the pipeline asked of it.</summary>
    internal SyntheticMediaSource? Source => _opened;

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

    /// <summary>The subtitle stream's index, when the script has one. The picture and the sound keep 0 and 1.</summary>
    internal const int SubtitleStreamIndex = 2;

    private int _next;
    private readonly Stopwatch _since = Stopwatch.StartNew();

    private static readonly TimeSpan LivePollMilliseconds = TimeSpan.FromMilliseconds(5);

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
            Duration = script.VideoDuration ?? script.Duration,
        };

        AudioStreamInfo audio = new()
        {
            Index = 1,
            CodecId = "synthetic-audio",
            SampleRate = script.SampleRate,
            ChannelCount = script.AudioChannelCount,
            ChannelLayout = script.AudioChannelLayout,
            SampleFormat = AudioSampleFormat.Int16,
            Duration = script.AudioDuration ?? script.Duration,
        };

        Info = new MediaInfo
        {
            ContainerId = "synthetic",
            Duration = script.Duration,
            Streams = script.Subtitles.Count == 0
                ? [video, audio]
                : [video, audio, new SubtitleStreamInfo { Index = SubtitleStreamIndex, CodecId = "synthetic-subtitle", Duration = script.Duration }],
        };

        int frameCount = (int)(script.Duration.TotalSeconds * script.FrameRate);
        for (int index = 0; index < frameCount; index++)
        {
            _timeline.Add(new ScriptedPacket(true, false, TimeSpan.FromSeconds((double)index / script.FrameRate), TimeSpan.FromSeconds(1.0 / script.FrameRate)));
        }

        int packetCount = (int)(script.Duration.TotalSeconds * script.SampleRate / script.SamplesPerAudioPacket);
        for (int index = 0; index < packetCount; index++)
        {
            var start = TimeSpan.FromSeconds((double)(index * script.SamplesPerAudioPacket) / script.SampleRate);
            var length = TimeSpan.FromSeconds((double)script.SamplesPerAudioPacket / script.SampleRate);
            _timeline.Add(new ScriptedPacket(false, false, start, length));
        }

        foreach (ScriptedSubtitle subtitle in script.Subtitles)
        {
            _timeline.Add(new ScriptedPacket(false, true, subtitle.Start, subtitle.Length));
        }

        // Interleaved the way a real file is, so the reader meets the two streams together.
        _timeline.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
    }

    public MediaInfo Info { get; }

    /// <summary>
    /// Readable again from the beginning, because a track chosen after the file is open restarts the read: the reader
    /// re-reads a position whenever the epoch moves, and a source that could not be re-read would lose everything the
    /// read-ahead had already taken (§2.5). The timeline is fixed, so a position is realised by starting over rather
    /// than by skipping — no test here seeks anywhere but the beginning.
    /// </summary>
    public bool CanSeek => true;

    public OperationResult Seek(TimeSpan position, int streamIndex)
    {
        LastSeekPosition = position;
        LastSeekStreamIndex = streamIndex;

        // The timeline is fixed and ordered, so a position is realised by starting at a packet rather than by
        // decoding and skipping.
        int next = _script.LandsOnTheKeyframeBefore ? LandOnTheKeyframeBefore(position, streamIndex) : FirstPacketAtOrAfter(position);
        _next = next < 0 ? _timeline.Count : next;
        return OperationResult.Success();
    }

    /// <summary>Where the pipeline last asked the cursor to go, and what it asked the landing to be measured by.</summary>
    internal TimeSpan? LastSeekPosition { get; private set; }

    /// <summary>The stream index the last seek was asked to land by, or -1 when the pipeline left it to this source.</summary>
    internal int LastSeekStreamIndex { get; private set; } = -1;

    private int FirstPacketAtOrAfter(TimeSpan position)
    {
        return _timeline.FindIndex(packet => packet.Timestamp >= position);
    }

    /// <summary>
    /// The last packet at or before the position of the stream the landing was asked to be measured by — every
    /// synthetic packet is a keyframe, so the last one is the keyframe a demuxer would fall back to. A position past
    /// the end of that stream lands on its last packet, however far behind that is: which stream it is decides
    /// everything the landing can be wrong by. Without a stream the picture is used, because a container's keyframes
    /// are the picture's.
    /// </summary>
    private int LandOnTheKeyframeBefore(TimeSpan position, int streamIndex)
    {
        bool? byVideo = streamIndex switch
        {
            0 => true,
            1 => false,
            _ => null,
        };

        int landing = -1;
        for (int index = 0; index < _timeline.Count; index++)
        {
            ScriptedPacket packet = _timeline[index];
            if (packet.Timestamp > position)
            {
                break;
            }

            // A packet this source withholds is not part of the file it declares: the landing cannot be on one.
            if (packet.Video && packet.Timestamp >= _script.VideoStopsAt)
            {
                continue;
            }

            // A subtitle event is not a keyframe and cannot be what a landing is measured by.
            if (packet.Subtitle)
            {
                continue;
            }

            if (byVideo is null ? packet.Video : packet.Video == byVideo.Value)
            {
                landing = index;
            }
        }

        return landing >= 0 ? landing : FirstPacketAtOrAfter(position);
    }

    public OperationResult TryReadPacket(out MediaPacket? packet)
    {
        while (true)
        {
            if (_closed.IsSet)
            {
                packet = null;
                return OperationResult.Success();
            }

            if (_next >= _timeline.Count)
            {
                if (_script.ReportsTheEndOfTheFile)
                {
                    packet = null;
                    return OperationResult.Success();
                }

                // A live source has no end to report: it waits for what comes next.
                _closed.Wait(20);
                continue;
            }

            // A live source hands over a packet when its moment comes rather than all at once, which is what makes it
            // live: the reader can be given media for as long as the test runs, and — because every wait here is short
            // and returns — a selection published in the meantime is taken up on the next packet rather than being
            // stuck behind a read that never returns.
            if (!_script.ReportsTheEndOfTheFile && _timeline[_next].Timestamp > _since.Elapsed)
            {
                TimeSpan due = _timeline[_next].Timestamp - _since.Elapsed;
                _closed.Wait(due < LivePollMilliseconds ? due : LivePollMilliseconds);
                continue;
            }

            ScriptedPacket scripted = _timeline[_next++];
            if (scripted.Video ? scripted.Timestamp >= _script.VideoStopsAt
                : scripted.Subtitle ? false
                : scripted.Timestamp >= _script.AudioStopsAt)
            {
                continue;
            }

            packet = new MediaPacket
            {
                StreamIndex = scripted.Video ? 0 : scripted.Subtitle ? SubtitleStreamIndex : 1,
                Timestamp = scripted.Timestamp,
                Duration = scripted.Length,
                IsKeyFrame = true,
                Buffer = new SyntheticMediaBuffer(),
            };

            return OperationResult.Success();
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

    private readonly record struct ScriptedPacket(bool Video, bool Subtitle, TimeSpan Timestamp, TimeSpan Length);
}

/// <summary>
/// A packet's worth of silence. It is planar like a decoder's own block, because what a renderer or a filter is
/// handed is described by its planes — a stand-in without them cannot be converted, so a test about a conversion
/// would say nothing.
/// </summary>
internal sealed class SyntheticMediaBuffer : IPlanarAudioBuffer
{
    private readonly byte[] _bytes;
    private readonly GCHandle _handle;

    internal SyntheticMediaBuffer(int byteCount = 0)
    {
        _bytes = new byte[Math.Max(byteCount, 0)];
        _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    public int PlaneCount => 1;

    public long Length => _bytes.Length;

    public nint GetPlanePointer(int plane)
    {
        return plane == 0 ? _handle.AddrOfPinnedObject() : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A synthetic block has one plane.");
    }

    public int GetPlaneByteCount(int plane)
    {
        return plane == 0 ? _bytes.Length : throw new ArgumentOutOfRangeException(nameof(plane), plane, "A synthetic block has one plane.");
    }

    public void Dispose()
    {
        _handle.Free();
    }
}

internal sealed class SyntheticVideoDecoderFactory : IVideoDecoderFactory
{
    public OperationResult TryCreate(IMediaSource source, VideoStreamInfo stream, DecoderOptions options, out IComponentCreator<IVideoDecoder>? creator)
    {
        creator = new FixedCreator<IVideoDecoder>(Signature, () => new Decoder(stream));
        return OperationResult.Success();
    }

    /// <summary>One backend, one kind of decoder, so the signature says which one this is and no more.</summary>
    internal string Signature => "synthetic:video";

    private sealed class Decoder(VideoStreamInfo stream) : IVideoDecoder
    {
        public string Name => "synthetic";
        public MediaStreamInfo Stream => stream;

        public string Signature => "synthetic:video";

        public bool IsHardwareAccelerated => false;

        public PixelFormat OutputFormat => PixelFormat.Yuv420P;

        public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawVideoFrame> frames)
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
}

internal sealed class SyntheticAudioDecoderFactory : IAudioDecoderFactory
{
    public OperationResult TryCreate(IMediaSource source, AudioStreamInfo stream, DecoderOptions options, out IComponentCreator<IAudioDecoder>? creator)
    {
        creator = new FixedCreator<IAudioDecoder>(Signature, () => new Decoder(stream));
        return OperationResult.Success();
    }

    /// <summary>One backend, one kind of decoder, so the signature says which one this is and no more.</summary>
    internal string Signature => "synthetic:audio";

    private sealed class Decoder(AudioStreamInfo stream) : IAudioDecoder
    {
        public string Name => "synthetic";
        public MediaStreamInfo Stream => stream;

        public string Signature => "synthetic:audio";

        public bool IsHardwareAccelerated => false;

        public AudioSampleFormat OutputFormat => AudioSampleFormat.Int16;

        public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<RawAudioBuffer> buffers)
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
                    Buffer = new SyntheticMediaBuffer((int)(packet.Duration.TotalSeconds * stream.SampleRate) * stream.ChannelCount * sizeof(short)),
                },
            ];

            return OperationResult.Success();
        }

        public OperationResult Drain(out IReadOnlyList<RawAudioBuffer> buffers)
        {
            buffers = [];
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
}

/// <summary>
/// Turns a scripted subtitle packet into one cue. The text is derived from the event's own time, so the same event read
/// twice produces the same text — which is what makes a duplicate recognizable, and what a re-read after a seek
/// produces.
/// </summary>
internal sealed class SyntheticSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    public OperationResult TryCreate(IMediaSource? source, SubtitleStreamInfo stream, out IComponentCreator<ISubtitleDecoder>? creator)
    {
        creator = new FixedCreator<ISubtitleDecoder>(Signature, () => new Decoder(stream));
        return OperationResult.Success();
    }

    /// <summary>One backend, one kind of decoder, so the signature says which one this is and no more.</summary>
    internal string Signature => "synthetic:subtitle";

    private sealed class Decoder(SubtitleStreamInfo stream) : ISubtitleDecoder
    {
        public string Name => "synthetic";

        public MediaStreamInfo Stream => stream;

        public bool IsHardwareAccelerated => false;

        public SubtitleCueFormat CueFormat => SubtitleCueFormat.Ass;

        public OperationResult TryDecode(MediaPacket packet, out IReadOnlyList<SubtitleCue> cues)
        {
            cues =
            [
                new SubtitleCue
                {
                    Start = packet.Timestamp,
                    End = packet.Timestamp + packet.Duration,
                    Text = SyntheticSubtitleDecoderFactory.TextAt(packet.Timestamp),
                },
            ];

            return OperationResult.Success();
        }

        public OperationResult Drain(out IReadOnlyList<SubtitleCue> cues)
        {
            cues = [];
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

    /// <summary>What the cue of an event starting at that moment says, so a test can recognise it.</summary>
    internal static string TextAt(TimeSpan start)
    {
        return $"cue@{start.TotalSeconds:0.###}";
    }
}