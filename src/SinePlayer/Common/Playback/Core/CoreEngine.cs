// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
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

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class CoreEngine : IDisposable
{
    #region Constructor

    private const string TAG = nameof(CoreEngine);

    private const int PositionIntervalMilliseconds = 250;

    private bool _disposed;
    private readonly AudioDeviceNotifier _audioDeviceNotifier = AudioDeviceNotifier.Shared;
    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _videoRenderTarget;
    private SwapChainPanel? _subtitlePanel;
    private SwapChainPanelRenderTarget? _subtitleRenderTarget;
    private GainAudioFilter? _gainFilter;
    private DownmixAudioFilter? _downmixFilter;

    private readonly PlaybackInfoHandle _playbackInfo = new();
    private bool _hasSource = false;
    private PlaybackComponents _components = PlaybackComponents.Default;
    private VideoTransform _videoTransform = VideoTransform.Identity;
    private SubtitlePresentation _subtitlePresentation = SubtitlePresentation.Identity;
    private double _volume = 1.0;
    private bool _muted;

    private bool _pendingReconcile = false;
    private ReconcileResult? _lastReconcile;

    public CoreEngine()
    {
        _audioDeviceNotifier.Subscribe(AudioDevices_Changed);
    }

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;
    public event Action? PositionChanged;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get
        {
            TimeSpan video = PlaybackInfo.SelectedVideoStream?.Duration ?? TimeSpan.Zero;
            TimeSpan audio = PlaybackInfo.SelectedAudioStream?.Duration ?? TimeSpan.Zero;
            TimeSpan longest = video > audio ? video : audio;
            if (longest > TimeSpan.Zero)
            {
                return longest;
            }

            return PlaybackInfo.Media?.Duration ?? TimeSpan.Zero;
        }
    }

    public double Volume => _volume;

    public bool IsMuted => _muted;

    public double PlaybackRate => 1.0;

    public PlaybackInfoHandle PlaybackInfo => _playbackInfo;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _audioDeviceNotifier.Unsubscribe(AudioDevices_Changed);

        StopPositionTimer();
        _playbackInfo.Detach();

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;
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

        _videoRenderTarget?.DetachSurface();
        _videoRenderTarget?.Dispose();
        _videoRenderTarget = null;
        _videoPanel = null;
        _subtitleRenderTarget?.DetachSurface();
        _subtitleRenderTarget?.Dispose();
        _subtitleRenderTarget = null;
        _subtitlePanel = null;
    }

    public async Task<OperationResult> Reset()
    {
        PlaybackPipeline pipeline = EnsurePipeline();

        OperationResult reset = await pipeline.Reset(EnsureVideoRenderTarget(), EnsureSubtitleRenderTarget());
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

        PlaybackPipeline pipeline = EnsurePipeline();

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

    public async Task<OperationResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return OperationResultExt.Failure(TAG, "There is nothing open to change a track of.");
        }

        OperationResult selected = await pipeline.SelectStream(kind, stream);
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

        // Posted, like everything else the pipeline offers, and the task is kept for three reasons: it says when
        // the position has moved, which for a seek while paused is the only chance the page gets to re-read it;
        // it says whether the source took the seek at all — a seek a source refuses leaves playback exactly
        // where it was, so there is nothing for the page to react to beyond the line in the log; and it says
        // when the seek is over, which is when a resumed playback has to start.
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

    public void SetVideoTransform(VideoTransform transform)
    {
        if (_videoTransform == transform)
        {
            return;
        }

        _videoTransform = transform;
        _pipeline?.VideoTransform = transform;
    }

    public void SetSubtitlePresentation(SubtitlePresentation presentation)
    {
        if (_subtitlePresentation == presentation)
        {
            return;
        }

        _subtitlePresentation = presentation;
        if (_pipeline is not null)
        {
            _pipeline.SubtitlePresentation = presentation;
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

    public UIElement CreateVideoSurface()
    {
        _videoPanel ??= new SwapChainPanel();
        return _videoPanel;
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

    #endregion

    #region Pipeline

    private PlaybackPipeline? _pipeline;

    private PlaybackPipeline EnsurePipeline()
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

        _components.Apply(pipeline);
        pipeline.VideoTransform = _videoTransform;
        pipeline.SubtitlePresentation = _subtitlePresentation;
        pipeline.StateChanged += Pipeline_StateChanged;

        GainAudioFilter gainFilter = new();
        gainFilter.SetLevel(_volume, _muted);
        pipeline.AudioChains[0].Filters.Add(gainFilter);
        _gainFilter = gainFilter;

        DownmixAudioFilter downmixFilter = new();
        pipeline.AudioChains[0].Filters.Add(downmixFilter);
        _downmixFilter = downmixFilter;

        // Warnings are heard as they happen — during the open for something left out of it, and later for a
        // device that stops moving. They were only ever looked at once, at load, before.
        pipeline.WarningRaised += Pipeline_WarningRaised;

        _playbackInfo.Attach(pipeline);
        _pipeline = pipeline;
        _pendingReconcile = true;
        return pipeline;
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
        if (pipeline is null || pipeline.AudioChains[0].SelectedStream is null)
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
        else if (!reconciled.Result.Audio.IsSuccessful)
        {
            Logger.W(TAG, $"The audio output could not be re-resolved: {reconciled.Result.Audio.Message}");
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

    private SwapChainPanelRenderTarget EnsureVideoRenderTarget()
    {
        _videoRenderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)CreateVideoSurface());
        return _videoRenderTarget;
    }

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
