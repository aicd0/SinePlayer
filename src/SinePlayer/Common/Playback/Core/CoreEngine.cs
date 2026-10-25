// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Playback.Surface;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
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
    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _renderTarget;
    private GainAudioFilter? _gainFilter;

    private readonly PlaybackInfoHandle _playbackInfo = new();
    private PlaybackComponents _components = PlaybackComponents.Default;
    private VideoTransform _videoTransform = VideoTransform.Identity;
    private double _volume = 1.0;
    private bool _muted;

    #endregion

    #region Public API

    public event Action<MediaEngineState, string?>? StateChanged;
    public event Action? PositionChanged;
    public event Action? MediaOpened;

    public MediaEngineState State => _pipeline?.State ?? MediaEngineState.None;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _pipeline?.Duration ?? TimeSpan.Zero;

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

        _renderTarget?.DetachSurface();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _videoPanel = null;
    }

    public async Task<ErrorResult> Load(string filePath)
    {
        var err = ErrorLogger.Create(TAG);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return err.Error($"Media file not found: '{filePath}'.");
        }

        PlaybackPipeline pipeline = EnsurePipeline();

        OperationResult opened = await pipeline.Open(MediaLocator.Parse(filePath), EnsureRenderTarget());
        if (!opened.IsSuccessful)
        {
            return err.Error($"Unable to open media file '{filePath}': {opened.Message}");
        }

        StartPositionTimer();
        RaiseMediaOpened();
        return err.Success();
    }

    public async Task<ErrorResult> SelectStream(MediaStreamKind kind, MediaStreamInfo? stream)
    {
        var err = ErrorLogger.Create(TAG);

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return err.Error("There is nothing open to change a track of.");
        }

        OperationResult selected = await pipeline.SelectStream(kind, stream);
        if (!selected.IsSuccessful)
        {
            string wanted = stream is null ? "no track" : $"track #{stream.Index}";
            return err.Error($"The {kind.ToString().ToLowerInvariant()} could not be changed to {wanted}. {selected.Message}");
        }

        return err.Success();
    }

    public async Task<ErrorResult> SetComponents(PlaybackComponents components)
    {
        var err = ErrorLogger.Create(TAG);
        ArgumentNullException.ThrowIfNull(components);

        if (_components == components)
        {
            return err.Success();
        }

        _components = components;

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return err.Success();
        }

        components.Apply(pipeline);

        OperationResult reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            return err.Error($"The components could not be changed. {reconciled.Message}");
        }

        return err.Success();
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

    public async Task Stop()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is not null)
        {
            await pipeline.Stop();
        }
    }

    public void Seek(TimeSpan position, SeekMode mode)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        TimeSpan duration = pipeline.Duration;
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

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0.0, 1.0);

        // The filter is where the level lives now, and it is this engine's own: setting it is all it takes, and
        // it applies to the next block rather than to what has already been handed to the device.
        _gainFilter?.SetLevel(_volume, _muted);
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _gainFilter?.SetLevel(_volume, _muted);
    }

    /// <summary>
    /// Not something the engine can do yet: the pipeline plays at the rate the file was written at, and
    /// reporting a rate it does not honour would be worse than saying so.
    /// </summary>
    public void SetPlaybackRate(double rate)
    {
        throw new NotSupportedException("This engine cannot change the playback rate yet.");
    }

    public UIElement CreateVideoSurface()
    {
        _videoPanel ??= new SwapChainPanel();
        return _videoPanel;
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
        pipeline.StateChanged += Pipeline_StateChanged;

        // The level of the sound is a filter, and the engine is the host of the pipeline, so it is the one that
        // makes the filter and holds it, set to what the player has already asked for before anything plays.
        GainAudioFilter gainFilter = new();
        gainFilter.SetLevel(_volume, _muted);
        pipeline.AudioFilters.Add(gainFilter);
        _gainFilter = gainFilter;

        // Warnings are heard as they happen — during the open for something left out of it, and later for a
        // device that stops moving. They were only ever looked at once, at load, before.
        pipeline.WarningRaised += Pipeline_WarningRaised;

        _playbackInfo.Attach(pipeline);
        _pipeline = pipeline;
        return pipeline;
    }

    /// <summary>
    /// Raised on the playback queue, which is where every event of the engine is raised, so this only logs:
    /// playing on with something left out is a warning, not a failure, and the page still gets its media.
    /// </summary>
    private void Pipeline_WarningRaised(string warning)
    {
        Logger.W(TAG, $"Playback warning: {warning}");
    }

    /// <summary>
    /// Passed on as it happens: the pipeline only reports a state it has actually changed to, so this is a
    /// translation and nothing else — one event in, one event out, with the reason filled in when there is one.
    /// <para>
    /// Raised on the playback queue, so the host hears about it on its own thread: it touches live data and the
    /// status bar, neither of which may be written from where playback runs.
    /// </para>
    /// </summary>
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

    private SwapChainPanelRenderTarget EnsureRenderTarget()
    {
        _renderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)CreateVideoSurface());
        return _renderTarget;
    }

    private void RaiseMediaOpened()
    {
        CoroutineUtils.RunInMainThread(() => MediaOpened?.Invoke());
    }

    private void RaisePositionChanged()
    {
        CoroutineUtils.RunInMainThread(() => PositionChanged?.Invoke());
    }

    #endregion
}
