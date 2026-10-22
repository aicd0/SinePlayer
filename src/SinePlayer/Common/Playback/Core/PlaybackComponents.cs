// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using SinePlayer.Common.Localization;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Backends.Direct3D11;
using SinePlayer.Core.Playback.Backends.FFmpeg;
using SinePlayer.Core.Playback.Backends.Null;
using SinePlayer.Core.Playback.Backends.Wasapi;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed record PlaybackComponentOption(string Key, string Label);

internal sealed record PlaybackComponents(
    string VideoDecoder,
    string AudioDecoder,
    string VideoRenderer,
    string AudioRenderer,
    bool VideoPreferHardwareDecoding)
{
    public const string AUTO = "auto";
    public const string NONE = "none";
    public const string FFMPEG = "ffmpeg";
    public const string DIRECT3D11 = "d3d11";
    private const string WASAPI_PREFIX = "wasapi:";

    public static PlaybackComponents Default { get; } = new(AUTO, AUTO, AUTO, AUTO, true);

    public static IReadOnlyList<PlaybackComponentOption> GetVideoDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
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

    public static IReadOnlyList<PlaybackComponentOption> GetVideoRendererOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(DIRECT3D11, "Direct3D 11"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static IReadOnlyList<PlaybackComponentOption> GetAudioRendererOptions()
    {
        List<PlaybackComponentOption> options =
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
        ];

        if (WasapiAudioEndpoints.TryGetRenderEndpoints(out IReadOnlyList<WasapiAudioEndpoint> endpoints, out _))
        {
            foreach (WasapiAudioEndpoint endpoint in endpoints)
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

    public void Apply(PlaybackPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        ApplyVideoDecoder(pipeline);
        ApplyAudioDecoder(pipeline, AudioDecoder);
        ApplyVideoRenderer(pipeline, VideoRenderer);
        ApplyAudioRenderer(pipeline, AudioRenderer);
    }

    private void ApplyVideoDecoder(PlaybackPipeline pipeline)
    {
        switch (VideoDecoder)
        {
            case FFMPEG:
                SetFactories<IVideoDecoderFactory>(pipeline, [new FFmpegVideoDecoderFactory()]);
                break;
            case NONE:
                SetFactories<IVideoDecoderFactory>(pipeline, [new NullVideoDecoderFactory()]);
                break;
            default:
                SetFactories<IVideoDecoderFactory>(pipeline, [new FFmpegVideoDecoderFactory(), new NullVideoDecoderFactory()]);
                break;
        }

        pipeline.Components.Preferences = pipeline.Components.Preferences with { PreferHardwareDecoding = VideoPreferHardwareDecoding };
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
                SetFactories<IAudioDecoderFactory>(pipeline, [new FFmpegAudioDecoderFactory(), new NullAudioDecoderFactory()]);
                break;
        }
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
                SetFactories<IVideoRendererFactory>(pipeline, [new D3D11VideoRendererFactory(), new NullVideoRendererFactory()]);
                break;
        }
    }

    private static void ApplyAudioRenderer(PlaybackPipeline pipeline, string key)
    {
        if (key.StartsWith(WASAPI_PREFIX, StringComparison.Ordinal))
        {
            string endpointId = key[WASAPI_PREFIX.Length..];
            if (WasapiAudioEndpoints.TryGetRenderEndpoints(out IReadOnlyList<WasapiAudioEndpoint> endpoints, out _))
            {
                foreach (WasapiAudioEndpoint endpoint in endpoints)
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
                    if (WasapiAudioEndpoints.TryGetRenderEndpoints(out IReadOnlyList<WasapiAudioEndpoint> endpoints, out _))
                    {
                        foreach (WasapiAudioEndpoint endpoint in endpoints)
                        {
                            factories.Add(WasapiAudioRendererFactory.For(endpoint));
                        }
                    }

                    if (factories.Count == 0)
                    {
                        factories.Add(new WasapiAudioRendererFactory());
                    }

                    factories.Add(new NullAudioRendererFactory());
                    SetFactories<IAudioRendererFactory>(pipeline, factories);
                    return;
                }
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
