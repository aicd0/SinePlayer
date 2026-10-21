// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

using SinePlayer.Common.BaseUI;
using SinePlayer.Common.BaseUI.PageAbilities;
using SinePlayer.Common.Playback.Surface;
using SinePlayer.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Views.Pages.PlaybackLab;

internal sealed partial class PlaybackLabPage : BasePage
{
    private static readonly string[] MEDIA_FILE_TYPES =
    [
        ".mp4", ".mkv", ".mov", ".webm", ".avi", ".ts", ".m4v",
        ".bmp", ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg",
    ];

    private static void ApplyComponentSelection<TFactory>(PlaybackPipeline pipeline, ComponentChoice? choice) where TFactory : class, IComponentFactory
    {
        if (choice is null)
        {
            return;
        }

        pipeline.Components.Clear<TFactory>();
        foreach (IComponentFactory factory in choice.Factories)
        {
            pipeline.Components.Register(factory);
        }

        if (choice.HardwareDecoding is bool hardwareDecoding)
        {
            // Choosing a software decoder is a different request, not just a different registration.
            pipeline.Components.Preferences = pipeline.Components.Preferences with { PreferHardwareDecoding = hardwareDecoding };
        }
    }

    private PlaybackPipeline? _pipeline;
    private GainAudioFilter? _gain;
    private SwapChainPanelRenderTarget? _renderTarget;
    private DispatcherQueueTimer? _statusTimer;
    private string _description = string.Empty;
    private string? _warning;
    private bool _updatingPickers;

    public PlaybackLabPage()
    {
        InitializeComponent();
        PopulatePickers();
    }

    protected override void OnStart(PageBundle bundle)
    {
        base.OnStart(bundle);

        GetMainPageAbility().SetTitle("Playback lab");
        GetMainPageAbility().SetIcon(new SymbolIconSource() { Symbol = Symbol.Repair });
    }

    protected override void OnStop()
    {
        base.OnStop();

        CloseMedia();
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    //
    // Events
    //

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Windows.Storage.StorageFile? file = await FilePickerUtils.PickFile(WindowId, MEDIA_FILE_TYPES);
        if (file is null)
        {
            return;
        }

        OpenMedia(file.Path);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyLevel();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyLevel();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
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
        RefreshStatus();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _pipeline?.Pause();
        RefreshStatus();
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(-10);
    }

    private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBy(30);
    }

    private async void VideoDecoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (_updatingPickers || pipeline is null || VideoDecoderPicker.SelectedItem is not ComponentChoice choice)
        {
            return;
        }

        ApplyComponentSelection<IVideoDecoderFactory>(pipeline, choice);
        await ReconcileAndReport(pipeline, "The video decoder");
        RefreshStatus();
    }

    private async void AudioDecoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (_updatingPickers || pipeline is null || AudioDecoderPicker.SelectedItem is not ComponentChoice choice)
        {
            return;
        }

        ApplyComponentSelection<IAudioDecoderFactory>(pipeline, choice);
        await ReconcileAndReport(pipeline, "The audio decoder");
        RefreshStatus();
    }

    private async void VideoRendererPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (_updatingPickers || pipeline is null || VideoRendererPicker.SelectedItem is not ComponentChoice choice)
        {
            return;
        }

        ApplyComponentSelection<IVideoRendererFactory>(pipeline, choice);
        await ReconcileAndReport(pipeline, "The renderer");
        RefreshStatus();
    }

    private async void AudioRendererPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (_updatingPickers || pipeline is null || AudioRendererPicker.SelectedItem is not ComponentChoice choice)
        {
            return;
        }

        ApplyComponentSelection<IAudioRendererFactory>(pipeline, choice);
        await ReconcileAndReport(pipeline, "The output device");
        RefreshStatus();
    }

    //
    // Pipeline
    //

    private async void OpenMedia(string path)
    {
        CloseMedia();

        _renderTarget ??= new SwapChainPanelRenderTarget(VideoPanel);

        // The renderer is created by the pipeline, on the thread that will use it: its graphics device
        // cannot be built here, because this thread is an STA that no other apartment can use.
        PlaybackPipeline pipeline = new()
        {
            SourceFactory = new FFmpegSourceProviderFactory(),
        };

        ApplyComponentSelection<IVideoDecoderFactory>(pipeline, VideoDecoderPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IAudioDecoderFactory>(pipeline, AudioDecoderPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IVideoRendererFactory>(pipeline, VideoRendererPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IAudioRendererFactory>(pipeline, AudioRendererPicker.SelectedItem as ComponentChoice);

        pipeline.StateChanged += Pipeline_StateChanged;
        pipeline.WarningRaised += Pipeline_WarningRaised;

        // The level is the host's now: this page makes the filter, adds it to the chain and sets it — which is
        // the whole of the volume API, and the reason no renderer below has to provide one.
        GainAudioFilter gain = new();
        gain.SetLevel(VolumeSlider.Value / 100.0, MuteToggle.IsChecked == true);
        pipeline.AudioFilters.Add(gain);

        // Awaited rather than blocked: setup attaches the presentation surface from the playback thread,
        // and the panel can only be touched from this one.
        PlaybackOperationResult opened = await pipeline.Open(MediaLocator.Parse(path), _renderTarget);
        if (!opened.IsSuccessful)
        {
            pipeline.StateChanged -= Pipeline_StateChanged;
            pipeline.WarningRaised -= Pipeline_WarningRaised;
            pipeline.Dispose();
            gain.Dispose();
            Report($"Opening the file failed. {opened.Message}");
            return;
        }

        _pipeline = pipeline;
        _gain = gain;
        _description = Describe(pipeline);

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
            pipeline.StateChanged -= Pipeline_StateChanged;
            pipeline.WarningRaised -= Pipeline_WarningRaised;
            pipeline.Dispose();
        }

        // The filter belongs to this page rather than to the pipeline, so letting go of the pipeline does not
        // end it: it is disposed here, once nothing is playing through it.
        GainAudioFilter? gain = _gain;
        _gain = null;
        gain?.Dispose();

        // The panel gives the surface back before the swap chain it is showing goes away.
        _renderTarget?.DetachSurface();
        _description = string.Empty;
        _warning = null;
    }

    private void ApplyLevel()
    {
        _gain?.SetLevel(VolumeSlider.Value / 100.0, MuteToggle.IsChecked == true);
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

    private void Pipeline_StateChanged(MediaEngineState state, string? error)
    {
        // Raised on the playback queue; the status line belongs to the UI thread.
        DispatcherQueue.TryEnqueue(RefreshStatus);
    }

    private void Pipeline_WarningRaised(string warning)
    {
        // Raised on the playback queue, and shown where the status line lives.
        DispatcherQueue.TryEnqueue(() => _warning = warning);
    }

    //
    // Timer
    //

    private void StartStatusTimer()
    {
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StopStatusTimer()
    {
        if (_statusTimer is null)
        {
            return;
        }

        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer = null;
    }

    private void StatusTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        RefreshStatus();
    }

    //
    // UI
    //

    private void PopulatePickers()
    {
        _updatingPickers = true;
        try
        {
            IVideoDecoderFactory ffmpegVideoDecoder = new FFmpegVideoDecoderFactory();
            IVideoDecoderFactory nullVideoDecoder = new NullVideoDecoderFactory();
            VideoDecoderPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [ffmpegVideoDecoder, nullVideoDecoder], HardwareDecoding: true),
                new("FFmpeg (hardware)", [ffmpegVideoDecoder], HardwareDecoding: true),
                new("FFmpeg (software)", [ffmpegVideoDecoder], HardwareDecoding: false),
                new("Nothing", [nullVideoDecoder]),
            };

            IAudioDecoderFactory ffmpegAudioDecoder = new FFmpegAudioDecoderFactory();
            IAudioDecoderFactory nullAudioDecoder = new NullAudioDecoderFactory();
            AudioDecoderPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [ffmpegAudioDecoder, nullAudioDecoder]),
                new("FFmpeg", [ffmpegAudioDecoder]),
                new("Nothing", [nullAudioDecoder]),
            };

            IVideoRendererFactory d3d11VideoRenderer = new D3D11VideoRendererFactory();
            IVideoRendererFactory nullVideoRenderer = new NullVideoRendererFactory();
            VideoRendererPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [d3d11VideoRenderer, nullVideoRenderer]),
                new("Direct3D 11", [d3d11VideoRenderer]),
                new("Nothing", [nullVideoRenderer]),
            };

            List<ComponentChoice> audioRenderers = [];
            List<IComponentFactory> allAudioRenderers = [];
            if (WasapiAudioEndpoints.TryGetRenderEndpoints(out IReadOnlyList<WasapiAudioEndpoint> endpoints, out string error))
            {
                foreach (WasapiAudioEndpoint endpoint in endpoints)
                {
                    IAudioRendererFactory device = WasapiAudioRendererFactory.For(endpoint);
                    allAudioRenderers.Add(device);
                    audioRenderers.Add(new ComponentChoice(
                        endpoint.IsDefault ? $"{endpoint.Name} (default)" : endpoint.Name,
                        [device]));
                }
            }
            else
            {
                Report($"Unable to list the audio output devices. {error}");
            }

            if (audioRenderers.Count == 0)
            {
                IAudioRendererFactory defaultDevice = new WasapiAudioRendererFactory();
                allAudioRenderers.Add(defaultDevice);
                audioRenderers.Add(new ComponentChoice("WASAPI (default)", [defaultDevice]));
            }

            IAudioRendererFactory nullAudioRenderer = new NullAudioRendererFactory();
            allAudioRenderers.Add(nullAudioRenderer);
            audioRenderers.Add(new ComponentChoice("Nothing", [nullAudioRenderer]));
            audioRenderers.Insert(0, new ComponentChoice("Auto", allAudioRenderers));
            AudioRendererPicker.ItemsSource = audioRenderers;

            VideoDecoderPicker.SelectedIndex = 0;
            AudioDecoderPicker.SelectedIndex = 0;
            VideoRendererPicker.SelectedIndex = 0;
            AudioRendererPicker.SelectedIndex = 0;
        }
        finally
        {
            _updatingPickers = false;
        }
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
        long underruns = pipeline.AudioUnderruns;
        string health = $" dropped {pipeline.DroppedFrameCount}, underruns {underruns}, lag {pipeline.PictureLag.TotalMilliseconds:0} ms.{waiting}";

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
        // Decode and render are the two candidates for whatever limits playback, so the line names both rather
        // than leaving the question to a guess — and each is named beside its cost, because "hardware decode"
        // and "the Direct3D renderer" are requests the machine may have answered differently.
        string decoding = pipeline.IsVideoHardwareAccelerated ? "on the GPU" : "on the CPU";
        string renderer = pipeline.VideoRendererName ?? "no renderer";
        string sound = pipeline.AudioRendererName is null ? string.Empty : $", sound by {pipeline.AudioRendererName}";
        string chain = pipeline.AudioFilters.Count == 0
            ? "no audio filters"
            : $"filters {string.Join(", ", pipeline.AudioFilters.Filters.Select(filter => filter.Name))}";
        string pace = $" decode {pipeline.AverageVideoDecodeMilliseconds:0.0} ms {decoding}, render {pipeline.AverageVideoRenderMilliseconds:0.0} ms by {renderer}{sound}, {chain}.";

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

    private async Task ReconcileAndReport(PlaybackPipeline pipeline, string what)
    {
        PlaybackOperationResult reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            Report($"{what} could not be changed. {reconciled.Message}");
        }
    }

    private void Report(string message)
    {
        if (message != StatusText.Text)
        {
            StatusText.Text = message;
        }
    }

    //
    // Utilities
    //

    private IMainPageAbilityForTab GetMainPageAbility()
    {
        return GetAbility<IMainPageAbilityForTab>()!;
    }

    //
    // Types
    //

    private sealed record ComponentChoice(string Name, IReadOnlyList<IComponentFactory> Factories, bool? HardwareDecoding = null)
    {
        public override string ToString()
        {
            return Name;
        }
    }
}
