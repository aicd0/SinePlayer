// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.ErrorHandling;
using SinePlayer.Common.Playback.WinUI;
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Internal.Timing;

namespace SinePlayer.Common.Playback;

/// <summary>
/// Presents <see cref="PlaybackPipeline"/> through the port the player page has always used. The page goes
/// on talking about states, events and a video surface, and everything the engine does differently — a swap
/// chain instead of an element, a pipeline instead of a player, threads of its own — stays on this side of
/// the port. This is the only file that knows both, which is what keeps replacing the engine from reaching
/// into the page.
/// </summary>
internal sealed class PlaybackEngineAdapter : IMediaEngine
{
    /// <summary>How often the position is reported while playing. The page throttles its own updates.</summary>
    private const int PositionIntervalMilliseconds = 250;

    private const string TAG = nameof(PlaybackEngineAdapter);

    private readonly D3D11VideoRendererFactory _videoRendererFactory = new();
    private readonly WasapiAudioRenderer _audioRenderer = new();

    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _renderTarget;
    private PlaybackPipeline? _pipeline;
    private DispatcherQueueTimer? _positionTimer;
    private MediaEngineState _state = MediaEngineState.None;
    private bool _isDisposed;

    public MediaEngineState State => _state;

    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _pipeline?.Duration ?? TimeSpan.Zero;

    public double Volume => _audioRenderer.Volume;

    public bool IsMuted => _audioRenderer.IsMuted;

    /// <summary>The rate the engine plays at, which is the one rate it can honestly report.</summary>
    public double PlaybackRate => 1.0;

    public event Action<MediaEngineState>? StateChanged;
    public event Action? PositionChanged;
    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? ErrorOccurred;

    public async Task<ErrorResult> Load(string filePath)
    {
        var err = ErrorLogger.Create(TAG);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return err.Error($"Media file not found: '{filePath}'.");
        }

        Close();

        SetState(MediaEngineState.Opening);
        try
        {
            PlaybackPipeline pipeline = CreatePipeline();
            _pipeline = pipeline;

            // Warnings are heard as they happen — during the open for something left out of it, and later for
            // a device that stops moving. They were only ever looked at once, at load, before.
            pipeline.WarningRaised += OnPipelineWarningRaised;

            // Awaited rather than blocked: setup attaches the presentation surface from the playback
            // thread, and the panel can only be touched from this one.
            PlaybackOperationResult opened = await pipeline.Open(MediaLocator.Parse(filePath), EnsureRenderTarget());
            if (!opened.IsSuccessful)
            {
                Close();
                SetState(MediaEngineState.Error);
                return err.Error($"Unable to open media file '{filePath}': {opened.Message}");
            }

            StartPositionTimer();
            RaiseMediaOpened();
            return err.Success();
        }
        catch (Exception ex)
        {
            Logger.E(TAG, $"Unable to load media file '{filePath}'", ex);
            Close();
            SetState(MediaEngineState.Error);
            return err.Error(ex);
        }
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
            // Asking to play once the file has run out means starting it again. The pipeline outlives the
            // end of the file, so a seek is all it takes.
            pipeline.Seek(TimeSpan.Zero);
        }

        pipeline.Play();
    }

    public void Pause()
    {
        _pipeline?.Pause();
    }

    /// <summary>Pauses and returns to the beginning, leaving the file open, as the page has always expected.</summary>
    public void Stop()
    {
        _pipeline?.Pause();
        _pipeline?.Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
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

        // Posted, like everything else the pipeline offers, and the task is kept for two reasons: it says when
        // the position has moved, which for a seek while paused is the only chance the page gets to re-read it,
        // and it says whether the source took the seek at all — a seek a source refuses leaves playback exactly
        // where it was, so there is nothing for the page to react to beyond the line in the log.
        CoroutineUtils.Run(async () =>
        {
            PlaybackOperationResult seeked = await pipeline.Seek(position);
            if (!seeked.IsSuccessful)
            {
                Logger.W(TAG, $"Seek to {position:g} was refused: {seeked.Message}");
            }

            if (ReferenceEquals(_pipeline, pipeline))
            {
                RaisePositionChanged();
            }
        });
    }

    public void SetVolume(double volume)
    {
        _audioRenderer.Volume = Math.Clamp(volume, 0.0, 1.0);
    }

    public void SetMuted(bool muted)
    {
        _audioRenderer.IsMuted = muted;
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Close();
        _audioRenderer.Dispose();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _videoPanel = null;
    }

    private PlaybackPipeline CreatePipeline()
    {
        // The components are chosen here rather than inside the pipeline, which is what lets the whole
        // backend be replaced without the pipeline noticing.
        PlaybackPipeline pipeline = new(
            new FFmpegSourceProviderFactory(),
            new FFmpegVideoDecoderFactory(),
            _videoRendererFactory,
            new FFmpegAudioDecoderFactory(),
            _audioRenderer,
            new DefaultSynchronizer());
        pipeline.StateChanged += OnPipelineStateChanged;
        return pipeline;
    }

    /// <summary>Lets go of the file and the surface, leaving the engine ready to load another.</summary>
    private void Close()
    {
        StopPositionTimer();

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is not null)
        {
            pipeline.StateChanged -= OnPipelineStateChanged;
            pipeline.WarningRaised -= OnPipelineWarningRaised;
            pipeline.Dispose();
        }

        // The panel gives the surface back before the swap chain it is showing goes away.
        _renderTarget?.DetachSurface();
    }

    private SwapChainPanelRenderTarget EnsureRenderTarget()
    {
        _renderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)CreateVideoSurface());
        return _renderTarget;
    }

    private void StartPositionTimer()
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || _positionTimer is not null)
        {
            return;
        }

        _positionTimer = dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionIntervalMilliseconds);
        _positionTimer.Tick += OnPositionTick;
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
        timer.Tick -= OnPositionTick;
    }

    private void OnPositionTick(DispatcherQueueTimer sender, object args)
    {
        if (State == MediaEngineState.Playing)
        {
            RaisePositionChanged();
        }
    }

    /// <summary>
    /// Raised on the playback queue, which is where every event of the engine is raised, so this only logs:
    /// playing on with something left out is a warning, not a failure, and the page still gets its media.
    /// </summary>
    private void OnPipelineWarningRaised(string warning)
    {
        Logger.W(TAG, $"Playback warning: {warning}");
    }

    /// <summary>
    /// Raised on the playback queue, so the page hears about it on its own thread: it touches live data and
    /// the status bar, neither of which may be written from where playback runs.
    /// </summary>
    private void OnPipelineStateChanged(MediaEngineState state, string? error)
    {
        SetState(state);

        if (state == MediaEngineState.Ended)
        {
            CoroutineUtils.RunInMainThread(() => MediaEnded?.Invoke());
        }
        else if (state == MediaEngineState.Error)
        {
            string message = error ?? "Playback failed.";
            Logger.E(TAG, $"Media playback failed: {message}", null);
            CoroutineUtils.RunInMainThread(() => ErrorOccurred?.Invoke(message));
        }
    }

    private void RaiseMediaOpened()
    {
        CoroutineUtils.RunInMainThread(() => MediaOpened?.Invoke());
    }

    private void RaisePositionChanged()
    {
        CoroutineUtils.RunInMainThread(() => PositionChanged?.Invoke());
    }

    private void SetState(MediaEngineState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        CoroutineUtils.RunInMainThread(() => StateChanged?.Invoke(state));
    }
}
