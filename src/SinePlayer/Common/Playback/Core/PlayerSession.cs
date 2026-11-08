// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Playback.Surface;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Downmix;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Data.Models.Misc;
using SinePlayer.Data.Models.Playback;

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class PlayerSession : IDisposable
{
    #region Constructor

    private const string TAG = nameof(PlayerSession);

    private const int PositionIntervalMilliseconds = 250;

    private bool _disposed;
    private readonly AudioDeviceNotifier _audioDeviceNotifier = AudioDeviceNotifier.Shared;
    private readonly List<PlayerView> _views = [];
    private SwapChainPanel? _subtitlePanel;
    private SwapChainPanelRenderTarget? _subtitleRenderTarget;
    private GainAudioFilter? _gainFilter;
    private DownmixAudioFilter? _downmixFilter;

    private IAudioChain? _audioChain;
    private ISubtitleChain? _subtitleChain;
    private bool _hasSource = false;
    private PlaybackComponents _components = PlaybackComponents.Default;
    private SubtitlePresentation _subtitlePresentation = SubtitlePresentation.Identity;
    private double _volume = 1.0;
    private bool _muted;

    private bool _pendingReconcile = false;
    private ReconcileResult? _lastReconcile;

    private PlayQueueModel _playQueue = PlayQueueModel.CreateEmpty();
    private bool _loaded;

    public PlayerSession(string id)
    {
        Id = id;
        _audioDeviceNotifier.Subscribe(AudioDevices_Changed);
    }

    #endregion

    #region Public API

    public string Id { get; }

    public PlayQueueModel PlayQueue => _playQueue;

    public PlaybackModel Playback { get; } = new();

    public event Action<MediaEngineState, string?>? StateChanged;

    public event Action? PositionChanged;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public IReadOnlyList<MediaSource> Sources => _pipeline?.Sources ?? [];

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get
        {
            TimeSpan video = _views.Count == 0
                ? TimeSpan.Zero
                : _views.Max(view => view.Chain?.SelectedStream?.Duration ?? TimeSpan.Zero);
            TimeSpan audio = _audioChain?.SelectedStream?.Duration ?? TimeSpan.Zero;
            TimeSpan longest = video > audio ? video : audio;
            if (longest > TimeSpan.Zero)
            {
                return longest;
            }

            IReadOnlyList<MediaSource> sources = Sources;
            return sources.Count == 0 ? TimeSpan.Zero : sources.Max(source => source.Info.Duration);
        }
    }

    public double Volume => _volume;

    public bool IsMuted => _muted;

    public double PlaybackRate => 1.0;

    public void Load(PlayQueueModel playQueue, string? serializedPlayback, long initialPositionMs)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _playQueue = playQueue;
        Playback.LoadState(playQueue, serializedPlayback, initialPositionMs);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _audioDeviceNotifier.Unsubscribe(AudioDevices_Changed);

        StopPositionTimer();

        foreach (PlayerView view in _views)
        {
            view.Detach();
        }

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;
        _audioChain = null;
        _subtitleChain = null;
        if (pipeline is not null)
        {
            pipeline.StateChanged -= Pipeline_StateChanged;
            pipeline.WarningRaised -= Pipeline_WarningRaised;
            pipeline.Dispose();
        }

        GainAudioFilter? gainFilter = _gainFilter;
        _gainFilter = null;
        gainFilter?.Dispose();

        DownmixAudioFilter? downmixFilter = _downmixFilter;
        _downmixFilter = null;
        downmixFilter?.Dispose();

        foreach (PlayerView view in _views)
        {
            view.Dispose();
        }

        _views.Clear();

        _subtitleRenderTarget?.DetachSurface();
        _subtitleRenderTarget?.Dispose();
        _subtitleRenderTarget = null;
        _subtitlePanel = null;
    }

    public async Task<OperationResult> Reset()
    {
        PlaybackPipeline pipeline = await EnsurePipeline();

        OperationResult reset = await pipeline.Reset();
        if (!reset.IsSuccessful)
        {
            return OperationResultExt.Failure(TAG, "The player could not be reset.", reset);
        }

        _hasSource = false;
        _pendingReconcile = true;
        return OperationResult.Success();
    }

    public void SetComponents(PlaybackComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (_components == components)
        {
            return;
        }

        _components = components;
        _pendingReconcile = true;

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is not null)
        {
            components.Apply(pipeline);
        }
    }

    public async Task<OperationResult<MediaSource>> AddSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return OperationResultExt.Failure<MediaSource>(TAG, $"Media file not found: '{filePath}'.");
        }

        PlaybackPipeline pipeline = await EnsurePipeline();

        var locator = MediaLocator.Parse(filePath);
        MediaSource? existing = pipeline.Sources.FirstOrDefault(source => string.Equals(source.Locator.Value, locator.Value, StringComparison.Ordinal));
        if (existing is not null)
        {
            return OperationResultExt.Success(existing);
        }

        OperationResult<MediaSource> added = await pipeline.AddSource(locator);
        if (!added.IsSuccessful)
        {
            return OperationResultExt.Failure<MediaSource>(
                TAG,
                $"Unable to open media file '{filePath}': {added.Message}",
                added.Exception,
                added.IsFatal);
        }

        if (!_hasSource)
        {
            _hasSource = true;
            StartPositionTimer();
        }

        _pendingReconcile = true;
        return added;
    }

    public PlayerView CreateView()
    {
        PlayerView view = new();
        _views.Add(view);
        return view;
    }

    public void RemoveView(PlayerView view)
    {
        if (!_views.Remove(view))
        {
            return;
        }

        if (_pipeline is not { } pipeline || view.Chain is not { } chain)
        {
            view.Dispose();
            return;
        }

        pipeline.RemoveChain(chain);
        _pendingReconcile = true;
        CoroutineUtils.Run(async () =>
        {
            await Reconcile();
            view.Dispose();
        });
    }

    public async Task<OperationResult> SelectVideoStream(PlayerView view, MediaStreamInfo? stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is no playback to change a track of.");
        }

        IVideoChain chain = await EnsureChain(pipeline, view);
        OperationResult selected = await pipeline.SelectStream(chain, stream);
        if (!selected.IsSuccessful)
        {
            string wanted = stream is null ? "no track" : $"track #{stream.Index}";
            return OperationResultExt.Failure(TAG, $"The picture could not be changed to {wanted}. {selected.Message}");
        }

        _pendingReconcile = true;
        return OperationResult.Success();
    }

    public async Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is nothing open to change a track of.");
        }

        IPlaybackChain? chain = ChainOf(kind);

        if (chain is null)
        {
            return OperationResultExt.Failure(TAG, $"There is no {kind.ToString().ToLowerInvariant()} to change a track of.");
        }

        OperationResult selected = await pipeline.SelectStream(chain, stream);
        if (!selected.IsSuccessful)
        {
            string wanted = stream is null ? "no track" : $"track #{stream.Index}";
            return OperationResultExt.Failure(TAG, $"The {kind.ToString().ToLowerInvariant()} could not be changed to {wanted}. {selected.Message}");
        }

        _pendingReconcile = true;
        return OperationResult.Success();
    }

    public async Task<OperationResult<ReconcileResult>> Reconcile()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure<ReconcileResult>(TAG, "There is nothing open to build.");
        }

        if (!_pendingReconcile && _lastReconcile is not null)
        {
            return OperationResultExt.Success(_lastReconcile);
        }

        OperationResult<ReconcileResult> reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            return OperationResultExt.Failure<ReconcileResult>(TAG, $"What was chosen could not be built. {reconciled.Message}");
        }

        _pendingReconcile = false;
        _lastReconcile = reconciled.Result;
        RaisePositionChanged();
        return reconciled;
    }

    public void Play()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        if (pipeline.State == MediaEngineState.Ended)
        {
            pipeline.Seek(TimeSpan.Zero);
        }

        pipeline.Play();
    }

    public void Pause()
    {
        _pipeline?.Pause();
    }

    public void Seek(TimeSpan position, SeekMode mode)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        TimeSpan duration = Duration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        CoroutineUtils.Run(async () =>
        {
            OperationResult seeked = await pipeline.Seek(position, mode);
            if (!seeked.IsSuccessful)
            {
                Logger.W(TAG, $"Seek to {position:g} ({mode}) was refused: {seeked.Message}");
            }

            if (ReferenceEquals(_pipeline, pipeline))
            {
                RaisePositionChanged();
            }
        });
    }

    public void SetSubtitlePresentation(SubtitlePresentation presentation)
    {
        if (_subtitlePresentation == presentation)
        {
            return;
        }

        _subtitlePresentation = presentation;
        if (_subtitleChain is { } subtitle)
        {
            subtitle.Presentation = presentation;
        }
    }

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0.0, 1.0);
        _gainFilter?.SetLevel(_volume, _muted);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _gainFilter?.SetLevel(_volume, _muted);
    }

    public void SetPlaybackRate(double rate)
    {
        throw new NotSupportedException("This engine cannot change the playback rate yet.");
    }

    public UIElement CreateSubtitleSurface()
    {
        // The surface sits above the video surface, so its opacity has to stay below 1. At 1 the two swap
        // chain panels are composed as opaque planes, and once the video panel's swap chain is rebound -
        // which is what switching the video track off and on does - the plane underneath stops being
        // composed at all: the picture is gone from the window while its swap chain keeps attaching and
        // presenting correct frames. Anything below 1 puts this surface in an alpha blended layer instead,
        // which composes correctly, and a tenth of a percent of transparency is imperceptible.
        _subtitlePanel ??= new SwapChainPanel { IsHitTestVisible = false, Opacity = 0.999 };
        return _subtitlePanel;
    }

    public OperationResult? ResultOf(ReconcileResult result, PlayerView view)
    {
        return view.Chain is { } chain ? result.VideoResults[chain] : null;
    }

    public OperationResult? ResultOf(ReconcileResult result, MediaStreamKind kind)
    {
        IPlaybackChain? chain = ChainOf(kind);
        if (chain is null)
        {
            return null;
        }

        if (chain is IVideoChain video)
        {
            return result.VideoResults[video];
        }

        if (chain is IAudioChain audio)
        {
            return result.AudioResults[audio];
        }

        return result.SubtitleResults[(ISubtitleChain)chain];
    }

    private IPlaybackChain? ChainOf(MediaStreamKind kind)
    {
        return kind switch
        {
            MediaStreamKind.Audio => _audioChain,
            MediaStreamKind.Subtitle => _subtitleChain,
            _ => null,
        };
    }

    #endregion

    #region Pipeline

    private PlaybackPipeline? _pipeline;

    private async Task<PlaybackPipeline> EnsurePipeline()
    {
        PlaybackPipeline? existing = _pipeline;
        if (existing is not null)
        {
            return existing;
        }

        PlaybackPipeline pipeline = new()
        {
            SourceFactory = new FFmpegSourceProviderFactory(),
        };

        IAudioChain audio = await pipeline.AddAudioChain();
        ISubtitleChain subtitle = await pipeline.AddSubtitleChain(EnsureSubtitleRenderTarget());
        _audioChain = audio;
        _subtitleChain = subtitle;

        foreach (PlayerView view in _views)
        {
            await CreateChain(pipeline, view);
        }

        _components.Apply(pipeline);
        subtitle.Presentation = _subtitlePresentation;
        pipeline.StateChanged += Pipeline_StateChanged;

        GainAudioFilter gainFilter = new();
        gainFilter.SetLevel(_volume, _muted);
        audio.Filters.Add(gainFilter);
        _gainFilter = gainFilter;

        DownmixAudioFilter downmixFilter = new();
        audio.Filters.Add(downmixFilter);
        _downmixFilter = downmixFilter;

        pipeline.WarningRaised += Pipeline_WarningRaised;

        _pipeline = pipeline;
        _pendingReconcile = true;
        return pipeline;
    }

    private async Task<IVideoChain> EnsureChain(PlaybackPipeline pipeline, PlayerView view)
    {
        if (view.Chain is { } existing)
        {
            return existing;
        }

        IVideoChain chain = await CreateChain(pipeline, view);
        _components.ApplyVideo(chain);
        _pendingReconcile = true;
        return chain;
    }

    private async Task<IVideoChain> CreateChain(PlaybackPipeline pipeline, PlayerView view)
    {
        IVideoChain chain = await pipeline.AddVideoChain(view.EnsureRenderTarget());
        view.Attach(pipeline, chain, _audioChain, _subtitleChain);
        return chain;
    }

    private void Pipeline_WarningRaised(IPlaybackChain? chain, string warning)
    {
        string source = chain is null ? string.Empty : $" ({chain.Kind})";
        Logger.W(TAG, $"Playback warning{source}: {warning}");
    }

    private void Pipeline_StateChanged(MediaEngineState state, string? error)
    {
        string? message = null;
        if (state == MediaEngineState.Error)
        {
            message = error ?? "Playback failed.";
            Logger.E(TAG, $"Media playback failed: {message}", null);
        }

        CoroutineUtils.RunInMainThread(() => StateChanged?.Invoke(state, message));
    }

    #endregion

    #region Audio device change

    private void AudioDevices_Changed()
    {
        CoroutineUtils.Run(RefreshAudioOutputAsync);
    }

    private async Task RefreshAudioOutputAsync()
    {
        if (_disposed)
        {
            return;
        }

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null || _audioChain?.SelectedStream is null)
        {
            return;
        }

        if (pipeline.State is not (MediaEngineState.Playing or MediaEngineState.Paused or MediaEngineState.Ended))
        {
            return;
        }

        _components.Apply(pipeline);

        OperationResult<ReconcileResult> reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            _pendingReconcile = true;
            Logger.W(TAG, $"The audio output could not be re-resolved: {reconciled.Message}");
        }
        else if (ResultOf(reconciled.Result, MediaStreamKind.Audio) is { IsSuccessful: false } audioFailure)
        {
            Logger.W(TAG, $"The audio output could not be re-resolved: {audioFailure.Message}");
        }
        else
        {
            _pendingReconcile = false;
            _lastReconcile = reconciled.Result;
        }
    }

    #endregion

    #region Position timer

    private DispatcherQueueTimer? _positionTimer;

    private void StartPositionTimer()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || _positionTimer is not null)
        {
            return;
        }

        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionIntervalMilliseconds);
        _positionTimer.Tick += PositionTimer_Tick;
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        DispatcherQueueTimer? timer = _positionTimer;
        _positionTimer = null;
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= PositionTimer_Tick;
    }

    private void PositionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (State == MediaEngineState.Playing)
        {
            RaisePositionChanged();
        }
    }

    #endregion

    #region Misc

    private SwapChainPanelRenderTarget EnsureSubtitleRenderTarget()
    {
        _subtitleRenderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)CreateSubtitleSurface());
        return _subtitleRenderTarget;
    }

    private void RaisePositionChanged()
    {
        CoroutineUtils.RunInMainThread(() => PositionChanged?.Invoke());
    }

    #endregion
}
