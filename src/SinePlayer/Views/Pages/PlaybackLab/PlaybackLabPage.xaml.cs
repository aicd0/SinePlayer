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
using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Common.Utils;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.Downmix;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Gain;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Backends.Win2D;
using SinePlayer.Core.Playback.Pipeline;
using SinePlayer.Helpers.Misc;

namespace SinePlayer.Views.Pages.PlaybackLab;

internal sealed partial class PlaybackLabPage : BasePage
{
    private const string TAG = nameof(PlaybackLabPage);

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
    private DownmixAudioFilter? _downmix;
    private SwapChainPanelRenderTarget? _videoRenderTarget;
    private SwapChainPanelRenderTarget? _subtitleRenderTarget;
    private DispatcherQueueTimer? _statusTimer;
    private string _description = string.Empty;
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
        _videoRenderTarget?.Dispose();
        _videoRenderTarget = null;
        _subtitleRenderTarget?.Dispose();
        _subtitleRenderTarget = null;
    }

    //
    // Events
    //

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            Windows.Storage.StorageFile? file = await FilePickerUtils.PickFile(WindowId, MEDIA_FILE_TYPES);
            if (file is null)
            {
                return;
            }

            await OpenMedia(file.Path);
        });
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyLevel();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyLevel();
    }

    private void TransformChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTransform();
    }

    private void TransformToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyTransform();
    }

    private void TransformSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ApplyTransform();
    }

    private void ResetTransformButton_Click(object sender, RoutedEventArgs e)
    {
        RotationPicker.SelectedIndex = 0;
        FlipHorizontalToggle.IsChecked = false;
        FlipVerticalToggle.IsChecked = false;
        FittingPicker.SelectedIndex = 0;
        ZoomSlider.Value = 100;
        PanXSlider.Value = 0;
        PanYSlider.Value = 0;

        ApplyTransform();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (pipeline is null)
            {
                return;
            }

            if (pipeline.State == MediaEngineState.Ended)
            {
                await pipeline.Seek(TimeSpan.Zero);
            }

            OperationResult played = await pipeline.Play();
            if (!played.IsSuccessful)
            {
                Logger.E(TAG, $"Playback was refused. {played.Message}");
            }

            RefreshStatus();
        });
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _pipeline?.Pause();
        RefreshStatus();
    }

    private void SeekBackButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(() => SeekBy(-10));
    }

    private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
    {
        CoroutineUtils.Run(() => SeekBy(30));
    }

    private void VideoDecoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || VideoDecoderPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<IVideoDecoderFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The video decoder");
            RefreshStatus();
        });
    }

    private void AudioDecoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || AudioDecoderPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<IAudioDecoderFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The audio decoder");
            RefreshStatus();
        });
    }

    private void VideoRendererPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || VideoRendererPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<IVideoRendererFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The renderer");
            RefreshStatus();
        });
    }

    private void AudioRendererPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || AudioRendererPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<IAudioRendererFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The output device");
            RefreshStatus();
        });
    }

    private void SubtitleDecoderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || SubtitleDecoderPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<ISubtitleDecoderFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The subtitle decoder");
            RefreshStatus();
        });
    }

    private void SubtitleRendererPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(async () =>
        {
            PlaybackPipeline? pipeline = _pipeline;
            if (_updatingPickers || pipeline is null || SubtitleRendererPicker.SelectedItem is not ComponentChoice choice)
            {
                return;
            }

            ApplyComponentSelection<ISubtitleRendererFactory>(pipeline, choice);
            await ReconcileAndLog(pipeline, "The subtitle renderer");
            RefreshStatus();
        });
    }

    private void VideoTrackPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(() => SelectTrack(MediaStreamKind.Video, VideoTrackPicker.SelectedItem as TrackChoice));
    }

    private void AudioTrackPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(() => SelectTrack(MediaStreamKind.Audio, AudioTrackPicker.SelectedItem as TrackChoice));
    }

    private void SubtitleTrackPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CoroutineUtils.Run(() => SelectTrack(MediaStreamKind.Subtitle, SubtitleTrackPicker.SelectedItem as TrackChoice));
    }

    //
    // Pipeline
    //

    private async Task OpenMedia(string path)
    {
        CloseMedia();

        _videoRenderTarget ??= new SwapChainPanelRenderTarget(VideoPanel);
        _subtitleRenderTarget ??= new SwapChainPanelRenderTarget(SubtitlePanel);

        // The renderer is created by the pipeline, on the thread that will use it: its graphics device
        // cannot be built here, because this thread is an STA that no other apartment can use.
        PlaybackPipeline pipeline = new()
        {
            SourceFactory = new FFmpegSourceProviderFactory(),
        };

        ApplyComponentSelection<IVideoDecoderFactory>(pipeline, VideoDecoderPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IVideoRendererFactory>(pipeline, VideoRendererPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IAudioDecoderFactory>(pipeline, AudioDecoderPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<IAudioRendererFactory>(pipeline, AudioRendererPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<ISubtitleDecoderFactory>(pipeline, SubtitleDecoderPicker.SelectedItem as ComponentChoice);
        ApplyComponentSelection<ISubtitleRendererFactory>(pipeline, SubtitleRendererPicker.SelectedItem as ComponentChoice);

        // What the transform controls say is what the file is opened with, so a picture that is turned stays
        // turned across files rather than snapping back on every open.
        pipeline.VideoTransform = CreateTransform();

        pipeline.StateChanged += Pipeline_StateChanged;
        pipeline.WarningRaised += Pipeline_WarningRaised;

        GainAudioFilter gain = new();
        gain.SetLevel(VolumeSlider.Value / 100.0, MuteToggle.IsChecked == true);
        pipeline.AudioFilters.Add(gain);

        DownmixAudioFilter downmix = new();
        pipeline.AudioFilters.Add(downmix);

        OperationResult opened = await pipeline.Open(
            MediaLocator.Parse(path),
            _videoRenderTarget,
            _subtitleRenderTarget);
        if (!opened.IsSuccessful)
        {
            pipeline.StateChanged -= Pipeline_StateChanged;
            pipeline.WarningRaised -= Pipeline_WarningRaised;
            pipeline.Dispose();
            gain.Dispose();
            downmix.Dispose();
            Logger.E(TAG, $"Opening the file failed. {opened.Message}");
            return;
        }

        _pipeline = pipeline;
        _gain = gain;
        _downmix = downmix;
        _description = Describe(pipeline);
        PopulateTrackPickers();

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

        GainAudioFilter? gain = _gain;
        _gain = null;
        gain?.Dispose();

        DownmixAudioFilter? downmix = _downmix;
        _downmix = null;
        downmix?.Dispose();

        // The panel gives the surface back before the swap chain it is showing goes away.
        _videoRenderTarget?.DetachSurface();
        _subtitleRenderTarget?.DetachSurface();
        _description = string.Empty;
        PopulateTrackPickers();
    }

    private async Task SelectTrack(MediaStreamKind kind, TrackChoice? choice)
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (_updatingPickers || pipeline is null || choice is null)
        {
            return;
        }

        OperationResult selected = await pipeline.SelectStream(kind, choice.Stream);
        if (!selected.IsSuccessful)
        {
            Logger.E(TAG, $"The track could not be changed. {selected.Message}");
        }

        PopulateTrackPickers();
        RefreshStatus();
    }

    private async Task SeekBy(double seconds)
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
        OperationResult seeked = await pipeline.Seek(target);
        if (!seeked.IsSuccessful)
        {
            Logger.E(TAG, $"The seek was refused. {seeked.Message}");
        }

        RefreshStatus();
    }

    private void ApplyLevel()
    {
        _gain?.SetLevel(VolumeSlider.Value / 100.0, MuteToggle.IsChecked == true);
    }

    /// <summary>
    /// What the transform controls say, as the one value the pipeline is given. Every control writes to this,
    /// so the value is always the whole of what the panel shows rather than a field-by-field patch.
    /// </summary>
    private VideoTransform CreateTransform()
    {
        var orientation = VideoOrientation.FromDegrees(
            Math.Max(0, RotationPicker.SelectedIndex) * 90,
            FlipHorizontalToggle.IsChecked == true,
            FlipVerticalToggle.IsChecked == true);

        VideoResizeMode resizeMode = FittingPicker.SelectedItem is VideoResizeMode mode ? mode : VideoResizeMode.Fit;

        return new VideoTransform(
            orientation,
            resizeMode,
            ZoomSlider.Value / 100.0,
            PanXSlider.Value / 100.0,
            PanYSlider.Value / 100.0);
    }

    private void ApplyTransform()
    {
        PlaybackPipeline? pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        // No reconcile and no new frame: the picture that is already there is drawn again through the new
        // transform, which is the whole of what turning it costs.
        pipeline.VideoTransform = CreateTransform();
        RefreshStatus();
    }

    private void Pipeline_StateChanged(MediaEngineState state, string? error)
    {
        if (error is not null)
        {
            Logger.E(TAG, error);
        }

        CoroutineUtils.PostInMainThread(RefreshStatus);
    }

    private void Pipeline_WarningRaised(string warning)
    {
        Logger.W(TAG, warning);
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

            IVideoRendererFactory d3d11VideoRenderer = new D3D11VideoRendererFactory();
            IVideoRendererFactory nullVideoRenderer = new NullVideoRendererFactory();
            VideoRendererPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [d3d11VideoRenderer, nullVideoRenderer]),
                new("Direct3D 11", [d3d11VideoRenderer]),
                new("Nothing", [nullVideoRenderer]),
            };

            IAudioDecoderFactory ffmpegAudioDecoder = new FFmpegAudioDecoderFactory();
            IAudioDecoderFactory nullAudioDecoder = new NullAudioDecoderFactory();
            AudioDecoderPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [ffmpegAudioDecoder, nullAudioDecoder]),
                new("FFmpeg", [ffmpegAudioDecoder]),
                new("Nothing", [nullAudioDecoder]),
            };

            ISubtitleDecoderFactory ffmpegSubtitleDecoder = new FFmpegSubtitleDecoderFactory();
            ISubtitleDecoderFactory nullSubtitleDecoder = new NullSubtitleDecoderFactory();
            SubtitleDecoderPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [ffmpegSubtitleDecoder, nullSubtitleDecoder]),
                new("FFmpeg", [ffmpegSubtitleDecoder]),
                new("Nothing", [nullSubtitleDecoder]),
            };

            ISubtitleRendererFactory win2dSubtitleRenderer = new Win2DSubtitleRendererFactory();
            ISubtitleRendererFactory nullSubtitleRenderer = new NullSubtitleRendererFactory();
            SubtitleRendererPicker.ItemsSource = new ComponentChoice[]
            {
                new("Auto", [win2dSubtitleRenderer, nullSubtitleRenderer]),
                new("Win2D", [win2dSubtitleRenderer]),
                new("Nothing", [nullSubtitleRenderer]),
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
                        endpoint.IsDefault ? $"WASAPI [{endpoint.Name}] (default)" : $"WASAPI {endpoint.Name}",
                        [device]));
                }
            }
            else
            {
                Logger.E(TAG, $"Unable to list the audio output devices. {error}");
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

            RotationPicker.ItemsSource = new[] { "0°", "90°", "180°", "270°" };
            RotationPicker.SelectedIndex = 0;
            FittingPicker.ItemsSource = Enum.GetValues<VideoResizeMode>();
            FittingPicker.SelectedIndex = 0;

            VideoDecoderPicker.SelectedIndex = 0;
            VideoRendererPicker.SelectedIndex = 0;
            AudioDecoderPicker.SelectedIndex = 0;
            AudioRendererPicker.SelectedIndex = 0;
            SubtitleDecoderPicker.SelectedIndex = 0;
            SubtitleRendererPicker.SelectedIndex = 0;
        }
        finally
        {
            _updatingPickers = false;
        }
    }

    private void PopulateTrackPickers()
    {
        static IEnumerable<TrackChoice> Prepend(TrackChoice first, IEnumerable<TrackChoice> rest)
        {
            yield return first;
            foreach (TrackChoice choice in rest)
            {
                yield return choice;
            }
        }

        static int IndexOf(ComboBox picker, MediaStreamInfo? stream)
        {
            if (picker.ItemsSource is not IEnumerable<TrackChoice> choices)
            {
                return -1;
            }

            int index = 0;
            foreach (TrackChoice choice in choices)
            {
                if (choice.Stream?.Index == stream?.Index)
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        _updatingPickers = true;
        try
        {
            PlaybackPipeline? pipeline = _pipeline;
            MediaInfo? info = pipeline?.MediaInfo;

            VideoTrackPicker.ItemsSource = info is null ? null : Prepend(new TrackChoice(null), info.VideoStreams.Select(stream => new TrackChoice(stream)));
            VideoTrackPicker.SelectedIndex = IndexOf(VideoTrackPicker, pipeline?.SelectedVideoStream);

            AudioTrackPicker.ItemsSource = info is null ? null : Prepend(new TrackChoice(null), info.AudioStreams.Select(stream => new TrackChoice(stream)));
            AudioTrackPicker.SelectedIndex = IndexOf(AudioTrackPicker, pipeline?.SelectedAudioStream);

            SubtitleTrackPicker.ItemsSource = info is null ? null : Prepend(new TrackChoice(null), info.SubtitleStreams.Select(stream => new TrackChoice(stream)));
            SubtitleTrackPicker.SelectedIndex = IndexOf(SubtitleTrackPicker, pipeline?.SelectedSubtitleStream);
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

        string surface = _videoRenderTarget is null
            ? string.Empty
            : $" Surface {_videoRenderTarget.SurfaceWidth}x{_videoRenderTarget.SurfaceHeight} at {_videoRenderTarget.RasterizationScale:0.##}x.";
        string waiting = pipeline.StarvedFor is null ? string.Empty : $" waiting for {pipeline.StarvedFor}.";
        long underruns = pipeline.AudioUnderruns;
        string health = $" dropped {pipeline.DroppedFrameCount}, underruns {underruns}, lag {pipeline.PictureLag.TotalMilliseconds:0} ms.{waiting}";
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
        string transform = DescribeTransform(pipeline.VideoTransform);
        string tracks = DescribeTracks(pipeline);

        string status = $"{pipeline.State}: {_description}. {pipeline.Position:mm\\:ss\\.fff} / {pipeline.Duration:mm\\:ss\\.fff}."
            + $" {presented} frame(s).{health}{compositor}{pace}{transform}{tracks}{surface}";
        if (status != StatusText.Text)
        {
            StatusText.Text = status;
        }
    }

    private static string DescribeTracks(PlaybackPipeline pipeline)
    {
        if (pipeline.SelectedVideoStream is null && pipeline.SelectedAudioStream is null)
        {
            return " No track is chosen: pick a video or audio track to play this file.";
        }

        MediaInfo? info = pipeline.MediaInfo;
        List<string> parts = [];
        if (info is not null && info.VideoStreams.Count > 1 && pipeline.SelectedVideoStream is { } video)
        {
            parts.Add($"video {DescribeTrack(video)}");
        }

        if (info is not null && info.AudioStreams.Count > 1 && pipeline.SelectedAudioStream is { } audio)
        {
            parts.Add($"audio {DescribeTrack(audio)}");
        }

        return parts.Count == 0 ? string.Empty : $" playing {string.Join(" and ", parts)}.";
    }

    /// <summary>Which stream is playing: its index, and the container's name for it when it has one.</summary>
    private static string DescribeTrack(MediaStreamInfo stream)
    {
        return stream.Title is { Length: > 0 } title ? $"#{stream.Index} \"{title}\"" : $"#{stream.Index}";
    }

    /// <summary>
    /// What the picture is being placed through, so a transform that looks wrong can be read off the line
    /// instead of guessed at from the picture.
    /// </summary>
    private static string DescribeTransform(VideoTransform transform)
    {
        string orientation = transform.Orientation.IsIdentity
            ? "upright"
            : $"{transform.Orientation.RotationDegrees}°{(transform.Orientation.Mirrored ? " mirrored" : string.Empty)}";

        return $" transform {orientation}, {transform.ResizeMode}, zoom {transform.Zoom:0.##}x, pan {transform.PanX:0.##}/{transform.PanY:0.##}.";
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

    private async Task ReconcileAndLog(PlaybackPipeline pipeline, string what)
    {
        OperationResult reconciled = await pipeline.Reconcile();
        if (!reconciled.IsSuccessful)
        {
            Logger.E(TAG, $"{what} could not be changed. {reconciled.Message}");
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

    private sealed record TrackChoice(MediaStreamInfo? Stream)
    {
        public override string ToString()
        {
            return Stream is null ? "Off" : MediaStreamLabel.Describe(Stream);
        }
    }
}
