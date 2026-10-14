// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.Playback.WinUI;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Playback;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Internal.Timing;

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
    private readonly WasapiAudioRenderer _audioRenderer = new();

    private PlaybackPipeline? _pipeline;
    private SwapChainPanelRenderTarget? _renderTarget;
    private DispatcherQueueTimer? _statusTimer;
    private string _description = string.Empty;

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
        _pipeline?.Play();
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
            _audioRenderer,
            new DefaultSynchronizer());
        pipeline.StateChanged += OnStateChanged;

        // Awaited rather than blocked: setup attaches the presentation surface from the playback thread,
        // and the panel can only be touched from this one.
        PlaybackResult opened = await pipeline.OpenAsync(MediaLocator.Parse(path), _renderTarget);
        if (!opened.IsSuccessful)
        {
            pipeline.StateChanged -= OnStateChanged;
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
            pipeline.Dispose();
        }

        // The panel gives the surface back before the swap chain it is showing goes away.
        _renderTarget?.DetachSurface();
        _description = string.Empty;

        PlayButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        SeekBackButton.IsEnabled = false;
        SeekForwardButton.IsEnabled = false;
    }

    private void SeekBy(double seconds)
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

        PlaybackResult seeked = pipeline.Seek(target);
        if (!seeked.IsSuccessful)
        {
            Report($"Seeking failed. {seeked.Message}");
            return;
        }

        RefreshStatus();
    }

    private void OnStateChanged()
    {
        // Raised on the playback thread; the status line belongs to the UI thread.
        DispatcherQueue.TryEnqueue(RefreshStatus);
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
        string warning = pipeline.Warning is null ? string.Empty : $" {pipeline.Warning}";

        // Drops and underruns are how "stutter" and "glitchy" turn into numbers: a trickle is the
        // synchronizer working, a torrent is the pipeline being starved. The lag says how far the picture
        // is behind the sound, which is what a picture that has stopped moving while sound plays shows.
        // "dropped" counts decoded frames that were never shown, and a stage being waited for is what the
        // clock standing still means: playback is not failing, it is waiting for something to work with.
        string waiting = pipeline.StarvedFor is null ? string.Empty : $" waiting for {pipeline.StarvedFor}.";
        string health = $" dropped {pipeline.DroppedFrameCount}, underruns {_audioRenderer.UnderrunCount}, lag {pipeline.PictureLag.TotalMilliseconds:0} ms.{waiting}";
        // Decode and render are the two candidates for whatever limits playback, so the line names both
        // rather than leaving the question to a guess.
        string pace = $" decode {pipeline.AverageDecodeMilliseconds:0.0} ms, render {pipeline.AverageRenderMilliseconds:0.0} ms.";

        Report($"{pipeline.State}: {_description}. {pipeline.Position:mm\\:ss\\.fff} / {pipeline.Duration:mm\\:ss\\.fff}."
            + $" {pipeline.PresentedFrameCount} frame(s).{health}{pace}{surface}{error}{warning}");
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
}
