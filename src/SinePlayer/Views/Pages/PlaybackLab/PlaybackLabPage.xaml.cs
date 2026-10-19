// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Playback.Surface;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Core.Playback.Timing;

namespace SinePlayer.Views.Pages.PlaybackLab;

/// <summary>
/// Developer harness for the playback engine. It drives <see cref="PlaybackPipeline"/> with the real
/// backends, so a file can be watched and heard without going through the player page — and so the
/// engine can be exercised in isolation from the rest of the application.
/// </summary>
internal sealed partial class PlaybackLabPage : BasePage
{
    private static readonly string[] MEDIA_FILE_TYPES =
    [
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".ts", ".m4v",
        ".bmp", ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg",
    ];

    private readonly D3D11VideoRendererFactory _videoRendererFactory = new();

    /// <summary>
    /// The sound's device is created by the pipeline that plays it, so it is captured here rather than held:
    /// this page only reads the device's underrun count for its status line and never disposes it.
    /// </summary>
    private readonly CapturingAudioRendererFactory _audioRendererFactory = new();

    private PlaybackPipeline? _pipeline;
    private SwapChainPanelRenderTarget? _renderTarget;
    private DispatcherQueueTimer? _statusTimer;
    private string _description = string.Empty;

    /// <summary>
    /// The last thing the pipeline said, kept for the status line. Warnings are lines rather than a value
    /// with a lifetime, so this only ever holds the most recent one, and a new one replaces it.
    /// </summary>
    private string? _warning;

    public PlaybackLabPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        Windows.Storage.StorageFile? file = await FilePickerUtils.PickFile(WindowId, MEDIA_FILE_TYPES);
        if (file is null)
        {
            return;
        }

        OpenMedia(file.Path);
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        if (pipeline.State == MediaEngineState.Ended)
        {
            // Asking to play once the file has run out means starting it again. The pipeline outlives the end
            // of the file, so a seek is all it takes — and it is needed, because a paused seek past the end of
            // the picture can end the file without the user asking to play it.
            pipeline.Seek(TimeSpan.Zero);
        }

        pipeline.Play();
        RefreshStatus();
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        _pipeline?.Pause();
        RefreshStatus();
    }

    private void OnSeekBackClicked(object sender, RoutedEventArgs e)
    {
        SeekBy(-10);
    }

    private void OnSeekForwardClicked(object sender, RoutedEventArgs e)
    {
        SeekBy(30);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CloseMedia();
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    private async void OpenMedia(string path)
    {
        CloseMedia();

        _renderTarget ??= new SwapChainPanelRenderTarget(VideoPanel);

        // The renderer is created by the pipeline, on the thread that will use it: its graphics device
        // cannot be built here, because this thread is an STA that no other apartment can use.
        PlaybackPipeline pipeline = new(
            new FFmpegSourceProviderFactory(),
            new FFmpegVideoDecoderFactory(),
            _videoRendererFactory,
            new FFmpegAudioDecoderFactory(),
            _audioRendererFactory,
            new DefaultSynchronizer());
        pipeline.StateChanged += OnStateChanged;
        pipeline.WarningRaised += OnWarningRaised;

        // Awaited rather than blocked: setup attaches the presentation surface from the playback thread,
        // and the panel can only be touched from this one.
        PlaybackOperationResult opened = await pipeline.Open(MediaLocator.Parse(path), _renderTarget);
        if (!opened.IsSuccessful)
        {
            pipeline.StateChanged -= OnStateChanged;
            pipeline.WarningRaised -= OnWarningRaised;
            pipeline.Dispose();
            Report($"Opening the file failed. {opened.Message}");
            return;
        }

        _pipeline = pipeline;
        _description = Describe(pipeline);

        PlayButton.IsEnabled = true;
        PauseButton.IsEnabled = true;
        SeekBackButton.IsEnabled = true;
        SeekForwardButton.IsEnabled = true;

        StartStatusTimer();
        RefreshStatus();
    }

    private void CloseMedia()
    {
        StopStatusTimer();

        PlaybackPipeline? pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is not null)
        {
            pipeline.StateChanged -= OnStateChanged;
            pipeline.WarningRaised -= OnWarningRaised;
            pipeline.Dispose();
        }

        // The panel gives the surface back before the swap chain it is showing goes away.
        _renderTarget?.DetachSurface();
        _description = string.Empty;
        _warning = null;

        PlayButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        SeekBackButton.IsEnabled = false;
        SeekForwardButton.IsEnabled = false;
    }

    private async void SeekBy(double seconds)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        TimeSpan target = pipeline.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (pipeline.Duration > TimeSpan.Zero && target > pipeline.Duration)
        {
            target = pipeline.Duration;
        }

        // Posted like the rest, and awaited here for both halves of the answer: where the seek landed, and
        // whether the source took it at all — a refused seek leaves playback where it was.
        //
        // The pipeline leaves a seek paused: what it reports is a fact about the file, never a wish to keep
        // playing. Whether the new position goes on playing is the host's call, and this page is the host —
        // so "a jump while playing goes on playing", which is what the buttons mean, is decided here. Read
        // before the seek is posted, because the answer is about what playback was doing when it was asked.
        bool wasPlaying = pipeline.State == MediaEngineState.Playing;

        PlaybackOperationResult seeked = await pipeline.Seek(target);
        if (!seeked.IsSuccessful)
        {
            Report($"The seek was refused. {seeked.Message}");
        }
        else if (wasPlaying)
        {
            await pipeline.Play();
        }

        RefreshStatus();
    }

    private void OnStateChanged(MediaEngineState state, string? error)
    {
        // Raised on the playback queue; the status line belongs to the UI thread.
        DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void OnWarningRaised(string warning)
    {
        // Raised on the playback queue, and shown where the status line lives.
        DispatcherQueue.TryEnqueue(() => _warning = warning);
    }

    private void StartStatusTimer()
    {
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
    }

    private void StopStatusTimer()
    {
        if (_statusTimer is null)
        {
            return;
        }

        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        _statusTimer = null;
    }

    private void OnStatusTick(DispatcherQueueTimer sender, object args)
    {
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        string surface = _renderTarget is null
            ? string.Empty
            : $" Surface {_renderTarget.SurfaceWidth}x{_renderTarget.SurfaceHeight} at {_renderTarget.RasterizationScale:0.##}x.";

        // The reason playback stopped is the most important thing on this line, so it is never omitted.
        string error = pipeline.Error is null ? string.Empty : $" Error: {pipeline.Error}";
        string warning = _warning is null ? string.Empty : $" {_warning}";

        // Drops and underruns are how "stutter" and "glitchy" turn into numbers: a trickle is the
        // synchronizer working, a torrent is the pipeline being starved. The lag says how far the picture
        // is behind the sound, which is what a picture that has stopped moving while sound plays shows.
        // "dropped" counts decoded frames that were never shown, and a stage being waited for is what the
        // clock standing still means: playback is not failing, it is waiting for something to work with.
        string waiting = pipeline.StarvedFor is null ? string.Empty : $" waiting for {pipeline.StarvedFor}.";
        string health = $" dropped {pipeline.DroppedFrameCount}, underruns {_audioRendererFactory.Created?.UnderrunCount ?? 0}, lag {pipeline.PictureLag.TotalMilliseconds:0} ms.{waiting}";

        // What the compositor did with them is the other half of the picture: frames the pipeline handed over
        // that never reached the display — presented, then superseded before the screen took them — are drops
        // no pipeline counter can see, so the display's own count is shown beside the pipeline's. Both are
        // read once here so the line describes one moment: the display's answer lags by the frame or two
        // still in flight, so the difference can briefly be short of the number it will settle at.
        long presented = pipeline.PresentedFrameCount;
        long? displayed = pipeline.DisplayedFrameCount;
        string compositor = displayed is null
            ? string.Empty
            : $" display took {displayed}, compositor dropped {Math.Max(0, presented - displayed.Value)}.";
        // Decode and render are the two candidates for whatever limits playback, so the line names both
        // rather than leaving the question to a guess.
        string pace = $" decode {pipeline.AverageDecodeMilliseconds:0.0} ms, render {pipeline.AverageRenderMilliseconds:0.0} ms.";

        Report($"{pipeline.State}: {_description}. {pipeline.Position:mm\\:ss\\.fff} / {pipeline.Duration:mm\\:ss\\.fff}."
            + $" {presented} frame(s).{health}{compositor}{pace}{surface}{error}{warning}");
    }

    private static string Describe(PlaybackPipeline pipeline)
    {
        MediaInfo? info = pipeline.MediaInfo;
        if (info is null)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (VideoStreamInfo video in info.VideoStreams)
        {
            parts.Add($"{video.CodecName ?? video.CodecId} {video.Width}x{video.Height} at {video.FrameRate:0.###} fps");
        }

        foreach (AudioStreamInfo audio in info.AudioStreams)
        {
            parts.Add($"{audio.CodecName ?? audio.CodecId} {audio.SampleRate} Hz {audio.ChannelCount} ch {audio.SampleFormat}");
        }

        return string.Join(", ", parts);
    }

    private void Report(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>
    /// Hands the pipeline a real renderer and keeps it for the status line, which reads the device's own
    /// underrun count. The pipeline owns the instance it was given and disposes it when the file closes; this
    /// only ever reads a counter, so a disposed instance still answers correctly.
    /// </summary>
    private sealed class CapturingAudioRendererFactory : IComponentFactory<IAudioRenderer>
    {
        public WasapiAudioRenderer? Created { get; private set; }

        public ComponentCapabilities Capabilities { get; } = new()
        {
            Name = "wasapi",
            Description = "Plays audio on the default output device through WASAPI in shared mode.",
        };

        public PlaybackOperationResult TryCreate(out IAudioRenderer? component)
        {
            WasapiAudioRenderer renderer = new();
            Created = renderer;
            component = renderer;
            return PlaybackOperationResult.Success;
        }
    }
}
