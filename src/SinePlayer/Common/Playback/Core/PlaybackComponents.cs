// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Backends.Win2D;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed record PlaybackComponentOption(string Key, string Label);

internal sealed record PlaybackComponents(
    string VideoDecoder,
    string VideoRenderer,
    string AudioDecoder,
    string AudioRenderer,
    string SubtitleDecoder,
    string SubtitleRenderer,
    bool VideoPreferHardwareDecoding)
{
    public const string AUTO = "auto";
    public const string NONE = "none";
    public const string FFMPEG = "ffmpeg";
    public const string DIRECT3D11 = "d3d11";
    public const string WIN2D = "win2d";
    private const string WASAPI_PREFIX = "wasapi:";

    public static PlaybackComponents Default { get; } = new(AUTO, AUTO, AUTO, AUTO, AUTO, AUTO, true);

    public static IReadOnlyList<PlaybackComponentOption> GetVideoDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static IReadOnlyList<PlaybackComponentOption> GetVideoRendererOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(DIRECT3D11, "Direct3D 11"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static IReadOnlyList<PlaybackComponentOption> GetAudioDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static IReadOnlyList<PlaybackComponentOption> GetAudioRendererOptions()
    {
        List<PlaybackComponentOption> options =
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
        ];

        OperationResult<IReadOnlyList<WasapiAudioEndpoint>> endpoints = WasapiAudioEndpoints.TryGetRenderEndpoints();
        if (endpoints.IsSuccessful)
        {
            foreach (WasapiAudioEndpoint endpoint in endpoints.Result)
            {
                options.Add(new(WASAPI_PREFIX + endpoint.Id,
                    endpoint.IsDefault ?
                        $"WASAPI [{endpoint.Name}] ({StringResourceProvider.Instance.Default})" :
                        $"WASAPI [{endpoint.Name}]"));
            }
        }

        options.Add(new(NONE, StringResourceProvider.Instance.None));
        return options;
    }

    public static IReadOnlyList<PlaybackComponentOption> GetSubtitleDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static IReadOnlyList<PlaybackComponentOption> GetSubtitleRendererOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(WIN2D, "Win2D"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public void Apply(PlaybackPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        ApplyVideoDecoder(pipeline, VideoDecoder);
        ApplyVideoRenderer(pipeline, VideoRenderer);
        ApplyAudioDecoder(pipeline, AudioDecoder);
        ApplyAudioRenderer(pipeline, AudioRenderer);
        ApplySubtitleDecoder(pipeline, SubtitleDecoder);
        ApplySubtitleRenderer(pipeline, SubtitleRenderer);
    }

    private void ApplyVideoDecoder(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetFactories<IVideoDecoderFactory>(pipeline, [new FFmpegVideoDecoderFactory()]);
                break;
            case NONE:
                SetFactories<IVideoDecoderFactory>(pipeline, [new NullVideoDecoderFactory()]);
                break;
            default:
                SetFactories<IVideoDecoderFactory>(pipeline, [new FFmpegVideoDecoderFactory()]);
                break;
        }

        pipeline.Components.Preferences = pipeline.Components.Preferences with { PreferHardwareDecoding = VideoPreferHardwareDecoding };
    }

    private static void ApplyVideoRenderer(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case DIRECT3D11:
                SetFactories<IVideoRendererFactory>(pipeline, [new D3D11VideoRendererFactory()]);
                break;
            case NONE:
                SetFactories<IVideoRendererFactory>(pipeline, [new NullVideoRendererFactory()]);
                break;
            default:
                SetFactories<IVideoRendererFactory>(pipeline, [new D3D11VideoRendererFactory()]);
                break;
        }
    }

    private static void ApplyAudioDecoder(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetFactories<IAudioDecoderFactory>(pipeline, [new FFmpegAudioDecoderFactory()]);
                break;
            case NONE:
                SetFactories<IAudioDecoderFactory>(pipeline, [new NullAudioDecoderFactory()]);
                break;
            default:
                SetFactories<IAudioDecoderFactory>(pipeline, [new FFmpegAudioDecoderFactory()]);
                break;
        }
    }

    private static void ApplyAudioRenderer(PlaybackPipeline pipeline, string key)
    {
        if (key.StartsWith(WASAPI_PREFIX, StringComparison.Ordinal))
        {
            string endpointId = key[WASAPI_PREFIX.Length..];
            OperationResult<IReadOnlyList<WasapiAudioEndpoint>> endpoints = WasapiAudioEndpoints.TryGetRenderEndpoints();
            if (endpoints.IsSuccessful)
            {
                foreach (WasapiAudioEndpoint endpoint in endpoints.Result)
                {
                    if (endpoint.Id == endpointId)
                    {
                        SetFactories<IAudioRendererFactory>(pipeline, [WasapiAudioRendererFactory.For(endpoint)]);
                        return;
                    }
                }
            }

            // The device is gone; the default one is what "the output device I picked" degrades to.
        }

        switch (key)
        {
            case NONE:
                SetFactories<IAudioRendererFactory>(pipeline, [new NullAudioRendererFactory()]);
                return;
            case AUTO:
            default:
                {
                    List<IComponentFactory> factories = [];
                    OperationResult<IReadOnlyList<WasapiAudioEndpoint>> endpoints = WasapiAudioEndpoints.TryGetRenderEndpoints();
                    if (endpoints.IsSuccessful)
                    {
                        foreach (WasapiAudioEndpoint endpoint in endpoints.Result)
                        {
                            factories.Add(WasapiAudioRendererFactory.For(endpoint));
                        }
                    }

                    if (factories.Count == 0)
                    {
                        factories.Add(new WasapiAudioRendererFactory());
                    }

                    SetFactories<IAudioRendererFactory>(pipeline, factories);
                    return;
                }
        }
    }

    private static void ApplySubtitleDecoder(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetFactories<ISubtitleDecoderFactory>(pipeline, [new FFmpegSubtitleDecoderFactory()]);
                break;
            case NONE:
                SetFactories<ISubtitleDecoderFactory>(pipeline, [new NullSubtitleDecoderFactory()]);
                break;
            default:
                SetFactories<ISubtitleDecoderFactory>(pipeline, [new FFmpegSubtitleDecoderFactory()]);
                break;
        }
    }

    private static void ApplySubtitleRenderer(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case WIN2D:
                SetFactories<ISubtitleRendererFactory>(pipeline, [new Win2DSubtitleRendererFactory()]);
                break;
            case NONE:
                SetFactories<ISubtitleRendererFactory>(pipeline, [new NullSubtitleRendererFactory()]);
                break;
            default:
                SetFactories<ISubtitleRendererFactory>(pipeline, [new Win2DSubtitleRendererFactory()]);
                break;
        }
    }

    private static void SetFactories<TFactory>(PlaybackPipeline pipeline, IEnumerable<IComponentFactory> factories) where TFactory : class, IComponentFactory
    {
        pipeline.Components.Clear<TFactory>();
        foreach (IComponentFactory factory in factories)
        {
            pipeline.Components.Register(factory);
        }
    }
}
