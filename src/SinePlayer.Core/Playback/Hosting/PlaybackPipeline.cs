// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics.CodeAnalysis;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Engine;
using SinePlayer.Core.Playback.Utils;

namespace SinePlayer.Core.Playback.Hosting;

public sealed partial class PlaybackPipeline : IDisposable, IChainHost
{
    #region Variables

    private readonly PipelineEvents _events;
    private readonly PipelineTimeline _timeline;
    private readonly MediaReader _reader;

    private readonly List<IChainFeed> _removedChains = [];
    private readonly AtomicCell<IReadOnlyDictionary<IPlaybackChain, IChainFeed>> _feeds =
        new(new Dictionary<IPlaybackChain, IChainFeed>());
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

    public double Rate
    {
        get => _timeline.Rate;
        set
        {
            double clamped = Math.Max(value, 0.0);
            _timeline.SetRate(clamped);
            foreach (AudioChain chain in _audioChains)
            {
                chain.SetSpeed(clamped);
            }
        }
    }

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

    Task<OperationResult> IChainHost.Select(IPlaybackChain chain, MediaStreamInfo? stream)
    {
        return _events.SubmitAndLog(nameof(SelectStreamCore), () => SelectStreamCore(chain, stream));
    }

    void IChainHost.OffsetChanged(IChainFeed chain)
    {
        if (_disposed)
        {
            return;
        }

        _reader.RequestResync();
        _reader.Wake();
    }

    void IChainHost.PictureLayoutChanged(IPictureFrameSource picture)
    {
        foreach (SubtitleChain subtitle in _subtitleChains)
        {
            if (subtitle.LayoutVideoChain == picture)
            {
                subtitle.RenderNode.Wake();
            }
        }
    }

    #endregion

    #region Queue workers

    private OperationResult ResetCore()
    {
        StopCore();
        _timeline.SeekTo(TimeSpan.Zero, SeekMode.PreviousKeyframe);
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
        VideoChain chain = new(this, _timeline, _events)
        {
            Target = target,
        };

        _videoChains = [.. _videoChains, chain];
        RegisterFeed(chain);
        return chain;
    }

    private AudioChain AddAudioChainCore()
    {
        AudioChain chain = new(this, _timeline, _events);
        chain.SetSpeed(_timeline.Rate);
        _audioChains = [.. _audioChains, chain];
        RegisterFeed(chain);
        return chain;
    }

    private SubtitleChain AddSubtitleChainCore(IVideoRenderTarget target)
    {
        SubtitleChain chain = new(this, _timeline, _events)
        {
            Target = target,
        };

        _subtitleChains = [.. _subtitleChains, chain];
        RegisterFeed(chain);
        return chain;
    }

    private OperationResult RemoveChainCore(IPlaybackChain chain)
    {
        if (!TryGetFeed(chain, out IChainFeed? feed))
        {
            return OperationResult.Failure("The chain is not one of this pipeline's.");
        }

        switch (feed.Kind)
        {
            case MediaStreamKind.Video:
                _videoChains = [.. _videoChains.Where(existing => existing != feed)];
                break;
            case MediaStreamKind.Audio:
                _audioChains = [.. _audioChains.Where(existing => existing != feed)];
                break;
            case MediaStreamKind.Subtitle:
                _subtitleChains = [.. _subtitleChains.Where(existing => existing != feed)];
                break;
        }

        UnregisterFeed(feed);
        _removedChains.Add(feed);
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

        MediaSource? source = stream is null ? null : FindSource(stream);
        if (stream is not null && source is null)
        {
            return OperationResult.Failure($"Stream {stream.Index} is not part of anything the pipeline was given.");
        }

        if (!TryGetFeed(chain, out IChainFeed? feed))
        {
            return OperationResult.Failure("The chain is not one of this pipeline's.");
        }

        feed.Select(source is null || stream is null ? null : new StreamBinding<MediaStreamInfo>(source, stream));
        return OperationResult.Success();
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
            .. _videoChains.Select(chain => chain.Selected?.Source),
            .. _audioChains.Select(chain => chain.Selected?.Source),
            .. _subtitleChains.Select(chain => chain.Selected?.Source),
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

        _timeline.SeekTo(position, mode);
        WakeAll();
        return OperationResult.Success();
    }

    private OperationResult<ReconcileResult> ReconcileCore()
    {
        static bool Reconcile(
            IPlaybackChain chain,
            OperationResult<ChainChange> reconciled,
            Dictionary<IPlaybackChain, OperationResult> results)
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

        Dictionary<IPlaybackChain, OperationResult> results = [];

        bool changed = false;
        foreach (VideoChain chain in _videoChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), results);
        }

        foreach (AudioChain chain in _audioChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), results);
        }

        foreach (SubtitleChain chain in _subtitleChains)
        {
            changed |= Reconcile(chain, chain.Reconcile(), results);
        }

        if (changed)
        {
            _timeline.Refresh();
            WakeAll();
        }

        if (_videoChains.Length + _audioChains.Length + _subtitleChains.Length > 0)
        {
            StartThreads();
        }

        return result.Success(new ReconcileResult
        {
            Results = results,
        });
    }

    private void FlushChainRemoval()
    {
        if (_removedChains.Count == 0)
        {
            return;
        }

        IChainFeed[] removed = [.. _removedChains];
        _removedChains.Clear();

        foreach (IChainFeed chain in removed)
        {
            RequestStopChain(chain);
            _reader.RemoveSource(chain);
        }

        _reader.Wake();

        foreach (IChainFeed chain in removed)
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
            if (subtitle.LayoutVideoChain is not { } layout ||
                !TryGetFeed(layout, out IChainFeed? feed) ||
                feed.Kind != MediaStreamKind.Video)
            {
                subtitle.LayoutVideoChain = null;
            }
        }
    }

    private void ReconcileClockOwner()
    {
        if (_timeline.DeviceOwner is { } owner && Array.IndexOf(_audioChains, owner) >= 0)
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
        _timeline.SeekTo(TimeSpan.Zero, SeekMode.PreviousKeyframe);

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
        IChainFeed[] removed = [.. _removedChains];
        _removedChains.Clear();
        foreach (IChainFeed chain in removed)
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

    #region Misc

    private void RegisterFeed(IChainFeed feed)
    {
        _feeds.Write(current => new Dictionary<IPlaybackChain, IChainFeed>(current) { [feed] = feed });
    }

    private void UnregisterFeed(IChainFeed feed)
    {
        _feeds.Write(current =>
        {
            if (!current.ContainsKey(feed))
            {
                return current;
            }

            Dictionary<IPlaybackChain, IChainFeed> next = new(current);
            next.Remove(feed);
            return next;
        });
    }

    private bool TryGetFeed(IPlaybackChain chain, [NotNullWhen(true)] out IChainFeed? feed)
    {
        if (_feeds.Read().TryGetValue(chain, out IChainFeed? found))
        {
            feed = found;
            return true;
        }

        feed = null;
        return false;
    }

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
