// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline : IDisposable
{
    #region Variables

    private readonly PipelineEvents _events;
    private readonly PipelineTimeline _timeline;
    private readonly MediaReader _reader;

    private readonly List<IPlaybackChain> _removedChains = [];
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

    public ISourceProviderFactory? SourceFactory { get; set; }

    public MediaEngineState State => _events.State;

    public string? Error => _events.Error;

    public IReadOnlyList<MediaSource> Sources => _sources;

    public TimeSpan Position => _timeline.Position;

    public string? StarvedFor
    {
        get
        {
            if (_videoChains.Any(chain => chain.Starved))
            {
                return "video";
            }

            return _audioChains.Any(chain => chain.Starved) ? "audio" : null;
        }
    }

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

    public Task<OperationResult> Reset()
    {
        return _events.SubmitAndLog(nameof(Reset), ResetCore);
    }

    public Task<OperationResult<MediaSource>> AddSource(MediaLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return _events.SubmitAndLog(nameof(AddSource), () => AddSourceCore(locator));
    }

    public Task<IVideoChain> AddVideoChain(IVideoRenderTarget target)
    {
        return _events.Submit<IVideoChain>(() => AddVideoChainCore(target));
    }

    public Task<IAudioChain> AddAudioChain()
    {
        return _events.Submit<IAudioChain>(AddAudioChainCore);
    }

    public Task<ISubtitleChain> AddSubtitleChain(IVideoRenderTarget target)
    {
        return _events.Submit<ISubtitleChain>(() => AddSubtitleChainCore(target));
    }

    public Task<OperationResult> RemoveChain(IPlaybackChain chain)
    {
        return _events.SubmitAndLog(nameof(RemoveChain), () => RemoveChainCore(chain));
    }

    public Task<OperationResult> SelectStream(IPlaybackChain chain, MediaStreamInfo? stream)
    {
        return _events.SubmitAndLog(nameof(SelectStream), () => SelectStreamCore(chain, stream));
    }

    public Task<OperationResult> Play()
    {
        return _events.SubmitAndLog(nameof(Play), PlayCore);
    }

    public Task<OperationResult> Pause()
    {
        return _events.SubmitAndLog(nameof(Pause), PauseCore);
    }

    public Task<OperationResult> Seek(TimeSpan position, SeekMode mode = SeekMode.PreviousKeyframe)
    {
        return _events.SubmitAndLog(nameof(Seek), () => SeekCore(position, mode));
    }

    public Task<OperationResult<ReconcileResult>> Reconcile()
    {
        return _events.SubmitAndLog(nameof(Reconcile), ReconcileCore);
    }

    public Task<OperationResult> Stop()
    {
        return _events.SubmitAndLog(nameof(Stop), ResetCore);
    }

    #endregion

    #region Queue workers

    private OperationResult ResetCore()
    {
        StopCore();

        _timeline.SetPosition(TimeSpan.Zero);
        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.PreviousKeyframe);

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

    private VideoChain AddVideoChainCore(IVideoRenderTarget target)
    {
        VideoChain chain = new(_timeline, _events, VideoTarget_SurfaceSizeChanged)
        {
            Target = target,
            LayoutChanged = NotifyLayoutChanged,
        };

        _videoChains = [.. _videoChains, chain];
        return chain;
    }

    private AudioChain AddAudioChainCore()
    {
        AudioChain chain = new(_timeline, _events);
        _audioChains = [.. _audioChains, chain];
        return chain;
    }

    private SubtitleChain AddSubtitleChainCore(IVideoRenderTarget target)
    {
        SubtitleChain chain = new(_timeline, _events, SubtitleTarget_SurfaceSizeChanged)
        {
            Target = target,
        };

        _subtitleChains = [.. _subtitleChains, chain];
        return chain;
    }

    private OperationResult RemoveChainCore(IPlaybackChain chain)
    {
        bool present =
            (chain is VideoChain heldVideo && Array.IndexOf(_videoChains, heldVideo) >= 0) ||
            (chain is AudioChain heldAudio && Array.IndexOf(_audioChains, heldAudio) >= 0) ||
            (chain is SubtitleChain heldSubtitle && Array.IndexOf(_subtitleChains, heldSubtitle) >= 0);
        if (!present)
        {
            return OperationResult.Failure("The chain is not one of this pipeline's.");
        }

        switch (chain)
        {
            case VideoChain video:
                _videoChains = [.. _videoChains.Where(existing => existing != video)];
                break;
            case AudioChain audio:
                _audioChains = [.. _audioChains.Where(existing => existing != audio)];
                break;
            case SubtitleChain subtitle:
                _subtitleChains = [.. _subtitleChains.Where(existing => existing != subtitle)];
                break;
        }

        _removedChains.Add(chain);
        return OperationResult.Success();
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

        bool chosen = _videoChains.Any(chain => chain.Source.Latest?.Stream is not null)
            || _audioChains.Any(chain => chain.Source.Latest?.Stream is not null);
        if (!chosen)
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

        if (!Enum.IsDefined(mode))
        {
            return OperationResult.Failure($"The seek mode {mode} is unknown.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return OperationResult.Failure($"Cannot seek when the state is {state}.");
        }

        MediaSource?[] sources =
        [
            .. _videoChains.Select(chain => chain.Selected.Source),
            .. _audioChains.Select(chain => chain.Selected.Source),
            .. _subtitleChains.Select(chain => chain.Selected.Source),
        ];
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
        static bool Reconcile<TChain>(
            TChain chain,
            OperationResult<ChainChange> reconciled,
            Dictionary<TChain, OperationResult> results) where TChain : class, IPlaybackChain
        {
            results[chain] = reconciled.IsSuccessful ? OperationResult.Success() : OperationResult.Failure(reconciled);
            return reconciled.IsSuccessful && reconciled.Result != ChainChange.None;
        }

        OperationResultCreator<ReconcileResult> result = new();

        if (_disposed)
        {
            return result.Failure("The pipeline has already been disposed.");
        }

        if (!_events.CheckState(out MediaEngineState state, MediaEngineState.Paused, MediaEngineState.Playing, MediaEngineState.Ended))
        {
            return result.Failure($"Cannot change the pipeline's components when the state is {state}.");
        }

        FlushChainRemoval();
        InstallSources();
        RebuildWakeTargets();
        ReconcileLayoutSources();
        ReconcileClockOwner();

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
        }

        if (_videoChains.Length + _audioChains.Length + _subtitleChains.Length > 0)
        {
            StartThreads();
        }

        return result.Success(new ReconcileResult
        {
            VideoResults = videoResults,
            AudioResults = audioResults,
            SubtitleResults = subtitleResults,
        });
    }

    private void FlushChainRemoval()
    {
        if (_removedChains.Count == 0)
        {
            return;
        }

        IPlaybackChain[] removed = [.. _removedChains];
        _removedChains.Clear();

        foreach (IPlaybackChain chain in removed)
        {
            RequestStopChain(chain);
            _reader.RemoveSource((IChainFeed)chain);
        }

        _reader.Wake();

        foreach (IPlaybackChain chain in removed)
        {
            JoinChain(chain);
            ResetChain(chain);
        }
    }

    private void InstallSources()
    {
        foreach (VideoChain chain in _videoChains)
        {
            _reader.AddVideoSource(chain, chain.Source);
        }

        foreach (AudioChain chain in _audioChains)
        {
            _reader.AddAudioSource(chain, chain.Source);
        }

        foreach (SubtitleChain chain in _subtitleChains)
        {
            _reader.AddSubtitleSource(chain, chain.Source);
        }
    }

    private void RebuildWakeTargets()
    {
        _wakeTargets = [_reader, .. _videoChains, .. _audioChains, .. _subtitleChains];
    }

    private void ReconcileLayoutSources()
    {
        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            if (subtitle.LayoutVideoChain is not VideoChain picture || Array.IndexOf(_videoChains, picture) < 0)
            {
                subtitle.LayoutVideoChain = null;
            }
        }
    }

    private void ReconcileClockOwner()
    {
        if (_timeline.DeviceOwner is AudioChain owner && Array.IndexOf(_audioChains, owner) >= 0)
        {
            return;
        }

        _timeline.DeviceOwner = _audioChains.Length > 0 ? _audioChains[0] : null;
    }

    private void StartThreads()
    {
        if (_host is null)
        {
            CoroutineHost host = new("Pipeline - Reader");
            host.Register(stop => _reader.Run(stop, NotifySourceEnded));
            host.Start();
            _host = host;
        }

        foreach (VideoChain chain in _videoChains)
        {
            StartChain(chain);
        }

        foreach (AudioChain chain in _audioChains)
        {
            StartChain(chain);
        }

        foreach (SubtitleChain chain in _subtitleChains)
        {
            StartChain(chain);
        }
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
        _timeline.AdvanceEpoch(TimeSpan.Zero, SeekMode.PreviousKeyframe);

        _events.SetState(MediaEngineState.None);
        return OperationResult.Success();
    }

    #endregion

    #region Chains

    private void StartChain(IPlaybackChain chain)
    {
        if (_host is not { } host || IsChainRunning(chain))
        {
            return;
        }

        switch (chain)
        {
            case VideoChain video:
                video.Start(_reader.Wake, (epoch, position) => IsStreamSpent(video.Source, video.ReadTail, epoch, position), TryNotifyPlaybackEnded);
                break;
            case AudioChain audio:
                audio.Start(host, _reader.Wake, (epoch, position) => IsStreamSpent(audio.Source, audio.ReadTail, epoch, position), TryNotifyPlaybackEnded);
                break;
            case SubtitleChain subtitle:
                subtitle.Start(host, _reader.Wake);
                break;
        }
    }

    private bool IsStreamSpent(ComponentSlot<MediaSource> slot, ChainTail tail, long epoch, TimeSpan position)
    {
        return HasEnded(slot.Latest?.Stream?.Duration ?? TimeSpan.Zero, position)
            || tail.IsSpent(epoch, position, FurthestTailTicksOf(slot.CurrentComponent));
    }

    private long FurthestTailTicksOf(MediaSource? source)
    {
        long furthest = -1;
        foreach (VideoChain chain in _videoChains)
        {
            if (chain.Source.CurrentComponent == source)
            {
                furthest = Math.Max(furthest, chain.ReadTail.Ticks);
            }
        }

        foreach (AudioChain chain in _audioChains)
        {
            if (chain.Source.CurrentComponent == source)
            {
                furthest = Math.Max(furthest, chain.ReadTail.Ticks);
            }
        }

        return furthest;
    }

    private static void RequestStopChain(IPlaybackChain chain)
    {
        switch (chain)
        {
            case VideoChain video:
                video.RequestStop();
                break;
            case AudioChain audio:
                audio.RequestStop();
                break;
            case SubtitleChain subtitle:
                subtitle.RequestStop();
                break;
        }
    }

    private static void JoinChain(IPlaybackChain chain)
    {
        switch (chain)
        {
            case VideoChain video:
                video.Join();
                break;
            case AudioChain audio:
                audio.Join();
                break;
            case SubtitleChain subtitle:
                subtitle.Join();
                break;
        }
    }

    private static void ResetChain(IPlaybackChain chain)
    {
        switch (chain)
        {
            case VideoChain video:
                video.Reset();
                break;
            case AudioChain audio:
                audio.Reset();
                break;
            case SubtitleChain subtitle:
                subtitle.Reset();
                break;
        }
    }

    private static bool IsChainRunning(IPlaybackChain chain)
    {
        return chain switch
        {
            VideoChain video => video.IsRunning,
            AudioChain audio => audio.IsRunning,
            SubtitleChain subtitle => subtitle.IsRunning,
            _ => false,
        };
    }

    #endregion

    #region Playback

    private bool IsStarved
    {
        get
        {
            return _videoChains.Any(chain => chain.Starved) || _audioChains.Any(chain => chain.Starved);
        }
    }

    private void StopThreads()
    {
        IPlaybackChain[] removed = [.. _removedChains];
        _removedChains.Clear();
        foreach (IPlaybackChain chain in removed)
        {
            RequestStopChain(chain);
        }

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

        foreach (IPlaybackChain chain in removed)
        {
            JoinChain(chain);
            ResetChain(chain);
        }
    }

    private void NotifySourceEnded(long epoch)
    {
        _timeline.EndingEpoch = epoch;
        TryNotifyPlaybackEnded();
    }

    private int ChooseSeekLanding(MediaSource source, TimeSpan position, SeekMode mode)
    {
        List<VideoStreamInfo> videos = [];
        foreach (VideoChain chain in _videoChains)
        {
            if (chain.Source.CurrentComponent == source && chain.Source.CurrentStream is VideoStreamInfo video)
            {
                videos.Add(video);
            }
        }

        AudioStreamInfo? audio = null;
        foreach (AudioChain chain in _audioChains)
        {
            if (chain.Source.CurrentComponent == source)
            {
                audio = (AudioStreamInfo?)chain.Source.CurrentStream;
                break;
            }
        }

        List<VideoStreamInfo> playing = [.. videos.Where(video => !HasEnded(video.Duration, position))];
        if (playing.Count > 0)
        {
            if (playing.Count == 1)
            {
                return playing[0].Index;
            }

            int[] candidates = [.. playing.Select(video => video.Index)];
            return source.Source.TryChooseSeekStream(candidates, position, mode, out int landing) ? landing : playing[0].Index;
        }

        if (videos.Count == 0)
        {
            return audio is null ? -1 : audio.Index;
        }

        if (audio is not null && !HasEnded(audio.Duration, position))
        {
            return audio.Index;
        }

        VideoStreamInfo last = videos.MaxBy(video => video.Duration)!;
        return audio is not null && audio.Duration > last.Duration ? audio.Index : last.Index;
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

        if (_videoChains.Any(chain => chain.Source.Latest?.Stream is not null && chain.EndedEpoch != endingEpoch)
            || _audioChains.Any(chain => chain.Source.Latest?.Stream is not null && chain.EndedEpoch != endingEpoch))
        {
            return;
        }

        _events.SetState(MediaEngineState.Ended);
        WakeAll();
    }

    private static bool HasEnded(TimeSpan duration, TimeSpan position)
    {
        return duration > TimeSpan.Zero && position > duration;
    }

    #endregion

    #region Events

    private void VideoTarget_SurfaceSizeChanged(VideoChain chain)
    {
        chain.RenderNode.Wake();
        NotifyLayoutChanged(chain);
    }

    private void SubtitleTarget_SurfaceSizeChanged(SubtitleChain chain)
    {
        Interlocked.Increment(ref chain.SurfaceVersion);
        chain.RenderNode.Wake();
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
            if (subtitle.LayoutVideoChain == picture)
            {
                subtitle.RenderNode.Wake();
            }
        }
    }

    private void NotifyClockMoved()
    {
        // Subtitle might be changed
        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            subtitle.RenderNode.Wake();
        }
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
