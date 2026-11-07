// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline : IDisposable
{
    #region Variables

    private readonly PipelineEvents _events;
    private readonly PipelineTimeline _timeline;
    private readonly MediaReader _reader;

    private volatile VideoChain[] _videoChains = [];
    private volatile AudioChain[] _audioChains = [];
    private volatile SubtitleChain[] _subtitleChains = [];
    private volatile IPipelineWakeTarget[] _wakeTargets;

    private volatile MediaSource[] _sources = [];
    private CoroutineHost? _host;

    private bool _disposed;

    #endregion

    #region Construction

    public PlaybackPipeline()
    {
        _events = new PipelineEvents(WakeAll);
        _timeline = new PipelineTimeline(NotifyClockMoved, () => IsStarved);
        _reader = new MediaReader(_timeline, _events, ChooseSeekLanding);
        _wakeTargets = [_reader];

        AddVideoChainCore(null);
        AddAudioChainCore();
        AddSubtitleChainCore(null);

        _timeline.DeviceOwner = Audio;
    }

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged
    {
        add => _events.StateChanged += value;
        remove => _events.StateChanged -= value;
    }

    public event Action<IPlaybackChain?, string>? WarningRaised
    {
        add => _events.WarningRaised += value;
        remove => _events.WarningRaised -= value;
    }

    public IReadOnlyList<IVideoChain> VideoChains => _videoChains;

    public IReadOnlyList<IAudioChain> AudioChains => _audioChains;

    public IReadOnlyList<ISubtitleChain> SubtitleChains => _subtitleChains;

    public VideoComponentResolver VideoComponents => Video.Components;

    public AudioComponentResolver AudioComponents => Audio.Components;

    public SubtitleComponentResolver SubtitleComponents => Subtitle.Components;

    public ISourceProviderFactory? SourceFactory { get; set; }

    public MediaEngineState State => _events.State;

    public string? Error => _events.Error;

    public IReadOnlyList<MediaSource> Sources => _sources;

    public TimeSpan Position => _timeline.Position;

    public string? StarvedFor => Video.Starved ? "video" : Audio.Starved ? "audio" : null;

    public void Dispose()
    {
        Task<OperationResult> teardown = _events.Submit(() =>
        {
            if (_disposed)
            {
                return OperationResult.Success();
            }

            _disposed = true;
            return StopCore();
        });

        teardown.Wait();
    }

    public Task<OperationResult> Reset(IVideoRenderTarget? videoTarget = null, IVideoRenderTarget? subtitleTarget = null)
    {
        return _events.SubmitAndLog(nameof(Reset), () => ResetCore(videoTarget, subtitleTarget));
    }

    public Task<OperationResult<MediaSource>> AddSource(MediaLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return _events.SubmitAndLog(nameof(AddSource), () => AddSourceCore(locator));
    }

    public Task<OperationResult> SelectStream(IPlaybackChain chain, MediaStreamInfo? stream)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return _events.SubmitAndLog(nameof(SelectStream), () => SelectStreamCore(chain, stream));
    }

    public Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        return SelectStream(FirstChainOf(kind), stream);
    }

    public Task<OperationResult> Play()
    {
        return _events.SubmitAndLog(nameof(Play), PlayCore);
    }

    public Task<OperationResult> Pause()
    {
        return _events.SubmitAndLog(nameof(Pause), PauseCore);
    }

    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.Fast)
    {
        return _events.SubmitAndLog(nameof(Seek), () => SeekCore(position, mode));
    }

    public Task<OperationResult<ReconcileResult>> Reconcile()
    {
        return _events.SubmitAndLog(nameof(Reconcile), ReconcileCore);
    }

    public Task<OperationResult> Stop()
    {
        return _events.SubmitAndLog(nameof(Stop), () => ResetCore(null, null));
    }

    #endregion

    #region Chains

    private VideoChain Video => _videoChains[0];

    private AudioChain Audio => _audioChains[0];

    private SubtitleChain Subtitle => _subtitleChains[0];

    private VideoChain AddVideoChainCore(IVideoRenderTarget? target)
    {
        VideoChain chain = new(_timeline, _events, VideoTarget_SurfaceSizeChanged)
        {
            Target = target,
            LayoutChanged = NotifyLayoutChanged,
        };

        _videoChains = [.. _videoChains, chain];
        _reader.AddVideoSource(chain, chain.Source);
        RebuildWakeTargets();
        return chain;
    }

    private AudioChain AddAudioChainCore()
    {
        AudioChain chain = new(_timeline, _events);

        _audioChains = [.. _audioChains, chain];
        _reader.AddAudioSource(chain, chain.Source);
        RebuildWakeTargets();
        return chain;
    }

    private SubtitleChain AddSubtitleChainCore(IVideoRenderTarget? target)
    {
        SubtitleChain chain = new(_timeline, _events, SubtitleTarget_SurfaceSizeChanged)
        {
            Target = target,
        };

        _subtitleChains = [.. _subtitleChains, chain];
        _reader.AddSubtitleSource(chain, chain.Source);
        RebuildWakeTargets();
        return chain;
    }

    private IPlaybackChain FirstChainOf(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Video => Video,
            MediaStreamKind.Audio => Audio,
            _ => Subtitle,
        };
    }

    private void RebuildWakeTargets()
    {
        _wakeTargets = [_reader, .. _videoChains, .. _audioChains, .. _subtitleChains];
    }

    #endregion

    #region Playback

    private OperationResult ResetCore(IVideoRenderTarget? videoTarget, IVideoRenderTarget? subtitleTarget)
    {
        StopCore();

        Video.Target = videoTarget;
        Subtitle.Target = subtitleTarget;
        Subtitle.LayoutSource = Video;

        _timeline.SetPosition(TimeSpan.Zero);
        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);

        _events.SetState(MediaEngineState.Paused);
        return OperationResult.Success();
    }

    private OperationResult<MediaSource> AddSourceCore(MediaLocator locator)
    {
        OperationResultCreator<MediaSource> result = new();

        if (_disposed)
        {
            return result.Failure("The pipeline has already been disposed.");
        }

        ISourceProviderFactory? sourceFactory = SourceFactory;
        if (sourceFactory is null)
        {
            return result.Failure("No source provider factory was registered.");
        }

        OperationResult opened = sourceFactory.TryOpen(locator, out IMediaSource? source);
        if (!opened.IsSuccessful)
        {
            return result.Failure(opened);
        }

        if (source is null)
        {
            return result.Failure("The source opened nothing to play.");
        }

        MediaSource added = new(source, locator);
        _sources = [.. _sources, added];
        return result.Success(added);
    }

    private OperationResult SelectStreamCore(IPlaybackChain chain, MediaStreamInfo? stream)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot change tracks when the state is {state}.");
        }

        if (stream is not null)
        {
            if (FindSource(stream) is null)
            {
                return OperationResult.Failure($"Stream {stream.Index} is not part of anything the pipeline was given.");
            }

            if (stream.Kind != chain.Kind)
            {
                return OperationResult.Failure($"Stream {stream.Index} is a {stream.Kind} stream, not a {chain.Kind} stream.");
            }
        }

        MediaSource? source = FindSource(stream);
        switch (chain)
        {
            case VideoChain video:
                video.Selected = new(source, (VideoStreamInfo?)stream);
                return OperationResult.Success();
            case AudioChain audio:
                audio.Selected = new(source, (AudioStreamInfo?)stream);
                return OperationResult.Success();
            case SubtitleChain subtitle:
                subtitle.Selected = new(source, (SubtitleStreamInfo?)stream);
                return OperationResult.Success();
            default:
                return OperationResult.Failure("The chain is not one of this pipeline's.");
        }
    }

    private OperationResult PlayCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (Video.Source.Latest?.Stream is null && Audio.Source.Latest?.Stream is null)
        {
            return OperationResult.Failure("No track is selected. Choose a video or audio track before playing.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused))
        {
            return OperationResult.Failure($"Cannot play when the state is {state}.");
        }

        _events.SetState(MediaEngineState.Playing);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult PauseCore()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Playing))
        {
            return OperationResult.Failure($"Cannot pause when the state is {state}.");
        }

        _events.SetState(MediaEngineState.Paused);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult SeekCore(TimeSpan position, SeekMode mode)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The pipeline has already been disposed.");
        }

        if (mode is not (SeekMode.Fast or SeekMode.Precise))
        {
            return OperationResult.Failure($"The seek mode {mode} is unknown.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot seek when the state is {state}.");
        }

        MediaSource?[] sources = [Video.Selected.Source, Audio.Selected.Source, Subtitle.Selected.Source];
        foreach (MediaSource source in sources.OfType<MediaSource>())
        {
            if (!source.Source.CanSeek)
            {
                return OperationResult.Failure($"A source cannot seek, so nothing was moved: {source.Locator}");
            }
        }

        if (state == MediaEngineState.Ended)
        {
            _events.SetState(MediaEngineState.Paused);
        }

        _timeline.AdvanceEpoch(position, mode);
        _timeline.SeekTo(position);

        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult<ReconcileResult> ReconcileCore()
    {
        OperationResultCreator<ReconcileResult> result = new();

        if (_disposed)
        {
            return result.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return result.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        Dictionary<IVideoChain, OperationResult> videoResults = [];
        Dictionary<IAudioChain, OperationResult> audioResults = [];
        Dictionary<ISubtitleChain, OperationResult> subtitleResults = [];

        bool changed = false;
        foreach (VideoChain chain in _videoChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), videoResults);
        }

        foreach (AudioChain chain in _audioChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), audioResults);
        }

        foreach (SubtitleChain chain in _subtitleChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), subtitleResults);
        }

        if (changed)
        {
            _timeline.AdvanceEpoch(_timeline.Position, SeekMode.Precise);
            WakeAll();

            if (_host is null)
            {
                StartThreads();
            }
        }

        return result.Success(new ReconcileResult
        {
            VideoResults = videoResults,
            AudioResults = audioResults,
            SubtitleResults = subtitleResults,
        });
    }

    private static bool Reconcile<TChain>(
        TChain chain,
        OperationResult<ChainChange> reconciled,
        Dictionary<TChain, OperationResult> results) where TChain : class, IPlaybackChain
    {
        results[chain] = reconciled.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(reconciled);
        return reconciled.IsSuccessful && reconciled.Result != ChainChange.None;
    }

    private OperationResult StopCore()
    {
        StopThreads();

        foreach (VideoChain video in _videoChains)
        {
            video.Reset();
        }

        foreach (AudioChain audio in _audioChains)
        {
            audio.Reset();
        }

        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            subtitle.Reset();
        }

        foreach (MediaSource source in _sources)
        {
            source.Source.Dispose();
        }

        _sources = [];

        _timeline.Stop();
        _timeline.EndingEpoch = -1;
        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.Fast);

        _events.SetState(MediaEngineState.None);
        return OperationResult.Success();
    }

    private void StartThreads()
    {
        CoroutineHost host = new("Pipeline - Reader");
        host.Register(stop => _reader.Run(stop, NotifySourceEnded));
        host.Start();
        _host = host;

        foreach (VideoChain video in _videoChains)
        {
            video.Start(_reader.Wake, IsVideoStreamSpent, TryNotifyPlaybackEnded);
        }

        foreach (AudioChain audio in _audioChains)
        {
            audio.Start(host, _reader.Wake, IsAudioStreamSpent, TryNotifyPlaybackEnded);
        }

        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            subtitle.Start(host, _reader.Wake);
        }
    }

    private void StopThreads()
    {
        _host?.RequestStop();

        foreach (VideoChain video in _videoChains)
        {
            video.RequestStop();
        }

        foreach (AudioChain audio in _audioChains)
        {
            audio.RequestStop();
        }

        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            subtitle.RequestStop();
        }

        _reader.Wake();

        _host?.Join();
        _host = null;

        foreach (VideoChain video in _videoChains)
        {
            video.Join();
        }

        foreach (AudioChain audio in _audioChains)
        {
            audio.Join();
        }

        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            subtitle.Join();
        }
    }
    private void NotifySourceEnded(long epoch)
    {
        _timeline.EndingEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private bool IsVideoStreamSpent(long epoch, TimeSpan position)
    {
        return Video.ReadTail.IsSpent(epoch, position, FurthestTailTicks);
    }

    private bool IsAudioStreamSpent(long epoch, TimeSpan position)
    {
        return Audio.ReadTail.IsSpent(epoch, position, FurthestTailTicks);
    }

    private long FurthestTailTicks => Math.Max(Video.ReadTail.Ticks, Audio.ReadTail.Ticks);

    private bool IsStarved => Video.Starved || Audio.Starved;

    private int ChooseSeekLanding(MediaSource source, TimeSpan position)
    {
        VideoStreamInfo? video = Video.Source.CurrentComponent == source ? (VideoStreamInfo?)Video.Source.CurrentStream : null;
        AudioStreamInfo? audio = Audio.Source.CurrentComponent == source ? (AudioStreamInfo?)Audio.Source.CurrentStream : null;

        if (video is null)
        {
            if (audio is null)
            {
                return -1;
            }

            return audio.Index;
        }

        if (audio is not null &&
            video.Duration > TimeSpan.Zero &&
            audio.Duration > video.Duration &&
            position > video.Duration)
        {
            return audio.Index;
        }

        return video.Index;
    }

    private void TryNotifyPlaybackEnded()
    {
        if (!_events.CheckState(out _, MediaEngineState.Playing, MediaEngineState.Paused))
        {
            return;
        }

        long endingEpoch = _timeline.EndingEpoch;
        if (endingEpoch != _timeline.Epoch)
        {
            return;
        }

        if (Video.Source.Latest?.Stream is not null && Video.EndedEpoch != endingEpoch)
        {
            return;
        }

        if (Audio.Source.Latest?.Stream is not null && Audio.EndedEpoch != endingEpoch)
        {
            return;
        }

        _events.SetState(MediaEngineState.Ended);
        WakeAll();
    }

    #endregion

    #region Events

    private void VideoTarget_SurfaceSizeChanged()
    {
        Video.RenderNode.Wake();
        Subtitle.RenderNode.Wake();
    }

    private void SubtitleTarget_SurfaceSizeChanged()
    {
        Interlocked.Increment(ref Subtitle.SurfaceVersion);
        Subtitle.RenderNode.Wake();
    }

    #endregion

    #region Communication

    private MediaSource? FindSource(MediaStreamInfo? stream)
    {
        if (stream is null)
        {
            return null;
        }

        foreach (MediaSource source in _sources)
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

    private void NotifyLayoutChanged(IPictureFrameSource picture)
    {
        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            if (ReferenceEquals(subtitle.LayoutSource, picture))
            {
                subtitle.RenderNode.Wake();
            }
        }
    }

    private void NotifyClockMoved()
    {
        // Subtitle might be changed
        Subtitle.RenderNode.Wake();
    }

    private void WakeAll()
    {
        foreach (IPipelineWakeTarget target in _wakeTargets)
        {
            target.Wake();
        }
    }

    #endregion
}
