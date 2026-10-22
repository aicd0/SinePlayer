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
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class CoreEngine : IDisposable
{
    private const string TAG = nameof(CoreEngine);

    private const int PositionIntervalMilliseconds = 250;

    private bool _disposed;
    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _renderTarget;
    private PlaybackPipeline? _pipeline;
    private GainAudioFilter? _gainFilter;
    private DispatcherQueueTimer? _positionTimer;

    private readonly PlaybackInfoHandle _playbackInfo = new();
    private PlaybackComponents _components = PlaybackComponents.Default;
    private MediaEngineState _state = MediaEngineState.None;
    private VideoTransform _videoTransform = VideoTransform.Identity;
    private double _volume = 1.0;
    private bool _muted;

    public event Action<MediaEngineState>? StateChanged;
    public event Action? PositionChanged;
    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<string>? ErrorOccurred;

    public MediaEngineState State => _state;

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

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;

        // Before the pipeline is disposed: a pane that is still open reads on its own timer, and the handle must
        // answer "nothing" from here on without reaching this object at all.
        _playbackInfo.Detach();

        if (pipeline is not null)
        {
            pipeline.StateChanged -= OnPipelineStateChanged;
            pipeline.WarningRaised -= OnPipelineWarningRaised;
            pipeline.Dispose();
        }

        // The filter belongs to this engine rather than to the pipeline, so letting go of the pipeline does not
        // end it: it is disposed here, once nothing is playing through it.
        GainAudioFilter? gainFilter = _gainFilter;
        _gainFilter = null;
        gainFilter?.Dispose();

        // The panel gives the surface back before the swap chain it is showing goes away.
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

        SetState(MediaEngineState.Opening);
        try
        {
            // The pipeline outlives the file. Opening another one stops what was playing through it and builds
            // the chains again, from the components it was last told to use.
            PlaybackPipeline pipeline = EnsurePipeline();

            // Awaited rather than blocked: setup attaches the presentation surface from the playback thread,
            // and the panel can only be touched from this one.
            PlaybackOperationResult opened = await pipeline.Open(MediaLocator.Parse(filePath), EnsureRenderTarget());
            if (!opened.IsSuccessful)
            {
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

            // A failure inside the open can leave the pipeline mid-teardown; stopping is what puts it back into a
            // state the next file can be opened in.
            await Stop();
            SetState(MediaEngineState.Error);
            return err.Error(ex);
        }
    }

    /// <summary>
    /// The components to play through. While a file is open the pipeline builds the chains the choice actually
    /// changes and keeps playing where it was — the same thing the playback lab does when a picker changes — so a
    /// component is never a reason to open the file again. With nothing open the choice is only recorded, and the
    /// next file is opened with it.
    /// </summary>
    public void SetComponents(PlaybackComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (_components == components)
        {
            return;
        }

        _components = components;

        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        components.Apply(pipeline);

        CoroutineUtils.Run(async () =>
        {
            PlaybackOperationResult reconciled = await pipeline.Reconcile();
            if (!reconciled.IsSuccessful)
            {
                // What is playing keeps playing on what it was already built with; the page is told why the
                // change did not happen rather than being left with a box that disagrees with the sound.
                string message = $"The components could not be changed. {reconciled.Message}";
                Logger.W(TAG, message);
                CoroutineUtils.RunInMainThread(() => ErrorOccurred?.Invoke(message));
            }
        });
    }

    /// <summary>
    /// Where the picture is placed: fitted by the chosen mode, turned and mirrored. It outlives the file being
    /// replaced, so it is held here and handed to the pipeline when one is made, the way the components are.
    /// </summary>
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

    /// <summary>
    /// Lets go of the file and of everything that was opened for it, keeping the pipeline itself — which is what
    /// makes it possible to be told about different components before the next file is opened. The position timer
    /// stays: it reads the state, so it says nothing while nothing is open.
    /// </summary>
    public async Task Stop()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is not null)
        {
            await pipeline.Stop();
        }
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

        // Posted, like everything else the pipeline offers, and the task is kept for three reasons: it says when
        // the position has moved, which for a seek while paused is the only chance the page gets to re-read it;
        // it says whether the source took the seek at all — a seek a source refuses leaves playback exactly
        // where it was, so there is nothing for the page to react to beyond the line in the log; and it says
        // when the seek is over, which is when a resumed playback has to start.
        CoroutineUtils.Run(async () =>
        {
            // The pipeline leaves a seek paused: what it reports is a fact about the file, not a wish to keep
            // playing. "A jump while playing goes on playing" is the page's expectation, and restoring it is
            // this layer's business — it is the host. Read as the seek is carried out rather than as it was
            // asked for, so a pause the user pressed while the seek was in flight is not overruled by this.
            bool wasPlaying = pipeline.State == MediaEngineState.Playing;

            PlaybackOperationResult seeked = await pipeline.Seek(position);
            if (!seeked.IsSuccessful)
            {
                Logger.W(TAG, $"Seek to {position:g} was refused: {seeked.Message}");
            }
            else if (wasPlaying)
            {
                // Refused or not, a source that will not be moved only means the position stayed: the sound
                // and the picture carry on where they were, which is what the user expects of a jump that
                // could not be made. Awaited, so the position the page re-reads below is the one playback
                // resumed at.
                await pipeline.Play();
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

    /// <summary>
    /// The one pipeline this engine plays everything through, made the first time a file is opened. The components
    /// are chosen here rather than inside the pipeline, which is what lets the whole backend be replaced without
    /// the pipeline noticing; what was chosen is the key the player settings wrote, and PlaybackComponents is what
    /// turns the four keys into the factories to register.
    /// </summary>
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
        pipeline.StateChanged += OnPipelineStateChanged;

        // The level of the sound is a filter, and the engine is the host of the pipeline, so it is the one that
        // makes the filter and holds it, set to what the player has already asked for before anything plays.
        GainAudioFilter gainFilter = new();
        gainFilter.SetLevel(_volume, _muted);
        pipeline.AudioFilters.Add(gainFilter);
        _gainFilter = gainFilter;

        // Warnings are heard as they happen — during the open for something left out of it, and later for a
        // device that stops moving. They were only ever looked at once, at load, before.
        pipeline.WarningRaised += OnPipelineWarningRaised;

        _playbackInfo.Attach(pipeline);
        _pipeline = pipeline;
        return pipeline;
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
