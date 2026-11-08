// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Libass;
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
    bool VideoPreferHardwareDecoding,
    bool VideoPreferHdr)
{
    public const string AUTO = "auto";
    public const string NONE = "none";
    public const string FFMPEG = "ffmpeg";
    public const string DIRECT3D11 = "d3d11";
    public const string WIN2D = "win2d";
    public const string LIBASS = "libass";
    private const string WASAPI_PREFIX = "wasapi:";

    public static PlaybackComponents Default { get; } = new(AUTO, AUTO, AUTO, AUTO, AUTO, AUTO, true, true);

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
            new(LIBASS, "libass"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public void Apply(PlaybackPipeline pipeline)
    {
        foreach (IVideoChain chain in pipeline.VideoChains)
        {
            ApplyVideo(chain);
        }

        ApplyAudioDecoder(pipeline, AudioDecoder);
        ApplyAudioRenderer(pipeline, AudioRenderer);
        ApplySubtitleDecoder(pipeline, SubtitleDecoder);
        ApplySubtitleRenderer(pipeline, SubtitleRenderer);
    }

    public void ApplyVideo(IVideoChain chain)
    {
        ApplyVideoDecoder(chain, VideoDecoder);
        ApplyVideoRenderer(chain, VideoRenderer);
    }

    private void ApplyVideoDecoder(IVideoChain chain, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetVideoDecoders(chain, [new FFmpegVideoDecoderFactory()]);
                break;
            case NONE:
                SetVideoDecoders(chain, [new NullVideoDecoderFactory()]);
                break;
            default:
                SetVideoDecoders(chain, [new FFmpegVideoDecoderFactory()]);
                break;
        }

        chain.Components.Preferences = chain.Components.Preferences with { PreferHardwareDecoding = VideoPreferHardwareDecoding };
    }

    private void ApplyVideoRenderer(IVideoChain chain, string key)
    {
        VideoSurfacePreference surface = VideoPreferHdr ? VideoSurfacePreference.Auto : VideoSurfacePreference.Sdr;

        switch (key)
        {
            case DIRECT3D11:
                SetVideoRenderers(chain, [new D3D11VideoRendererFactory(surface)]);
                break;
            case NONE:
                SetVideoRenderers(chain, [new NullVideoRendererFactory()]);
                break;
            default:
                SetVideoRenderers(chain, [new D3D11VideoRendererFactory(surface)]);
                break;
        }
    }

    private static void ApplyAudioDecoder(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetAudioDecoders(pipeline, [new FFmpegAudioDecoderFactory()]);
                break;
            case NONE:
                SetAudioDecoders(pipeline, [new NullAudioDecoderFactory()]);
                break;
            default:
                SetAudioDecoders(pipeline, [new FFmpegAudioDecoderFactory()]);
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
                        SetAudioRenderers(pipeline, [WasapiAudioRendererFactory.For(endpoint)]);
                        return;
                    }
                }
            }

            // The device is gone; the default one is what "the output device I picked" degrades to.
        }

        switch (key)
        {
            case NONE:
                SetAudioRenderers(pipeline, [new NullAudioRendererFactory()]);
                return;
            case AUTO:
            default:
                {
                    List<IAudioRendererFactory> factories = [];
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

                    SetAudioRenderers(pipeline, factories);
                    return;
                }
        }
    }

    private static void ApplySubtitleDecoder(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetSubtitleDecoders(pipeline, [new FFmpegSubtitleDecoderFactory()]);
                break;
            case NONE:
                SetSubtitleDecoders(pipeline, [new NullSubtitleDecoderFactory()]);
                break;
            default:
                SetSubtitleDecoders(pipeline, [new FFmpegSubtitleDecoderFactory()]);
                break;
        }
    }

    private static void ApplySubtitleRenderer(PlaybackPipeline pipeline, string key)
    {
        switch (key)
        {
            case WIN2D:
                SetSubtitleRenderers(pipeline, [new Win2DSubtitleRendererFactory()]);
                break;
            case LIBASS:
                SetSubtitleRenderers(pipeline, [new AssSubtitleRendererFactory()]);
                break;
            case NONE:
                SetSubtitleRenderers(pipeline, [new NullSubtitleRendererFactory()]);
                break;
            default:
                SetSubtitleRenderers(pipeline, [new AssSubtitleRendererFactory(), new Win2DSubtitleRendererFactory()]);
                break;
        }
    }

    private static void SetVideoDecoders(IVideoChain chain, IEnumerable<IVideoDecoderFactory> factories)
    {
        chain.Components.ClearDecoders();
        foreach (IVideoDecoderFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetVideoRenderers(IVideoChain chain, IEnumerable<IVideoRendererFactory> factories)
    {
        chain.Components.ClearRenderers();
        foreach (IVideoRendererFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetAudioDecoders(PlaybackPipeline pipeline, IEnumerable<IAudioDecoderFactory> factories)
    {
        pipeline.AudioChains[0].Components.ClearDecoders();
        foreach (IAudioDecoderFactory factory in factories)
        {
            pipeline.AudioChains[0].Components.Register(factory);
        }
    }

    private static void SetAudioRenderers(PlaybackPipeline pipeline, IEnumerable<IAudioRendererFactory> factories)
    {
        pipeline.AudioChains[0].Components.ClearRenderers();
        foreach (IAudioRendererFactory factory in factories)
        {
            pipeline.AudioChains[0].Components.Register(factory);
        }
    }

    private static void SetSubtitleDecoders(PlaybackPipeline pipeline, IEnumerable<ISubtitleDecoderFactory> factories)
    {
        pipeline.SubtitleChains[0].Components.ClearDecoders();
        foreach (ISubtitleDecoderFactory factory in factories)
        {
            pipeline.SubtitleChains[0].Components.Register(factory);
        }
    }

    private static void SetSubtitleRenderers(PlaybackPipeline pipeline, IEnumerable<ISubtitleRendererFactory> factories)
    {
        pipeline.SubtitleChains[0].Components.ClearRenderers();
        foreach (ISubtitleRendererFactory factory in factories)
        {
            pipeline.SubtitleChains[0].Components.Register(factory);
        }
    }
}
