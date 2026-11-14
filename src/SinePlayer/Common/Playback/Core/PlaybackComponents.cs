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
using SinePlayer.Core.Playback.Hosting;

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

    public static PlaybackComponentOption[] GetVideoDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static PlaybackComponentOption[] GetVideoRendererOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(DIRECT3D11, "Direct3D 11"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static PlaybackComponentOption[] GetAudioDecoderOptions()
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

    public static PlaybackComponentOption[] GetSubtitleDecoderOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(FFMPEG, "FFmpeg"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public static PlaybackComponentOption[] GetSubtitleRendererOptions()
    {
        return
        [
            new(AUTO, StringResourceProvider.Instance.Auto),
            new(WIN2D, "Win2D"),
            new(LIBASS, "libass"),
            new(NONE, StringResourceProvider.Instance.None),
        ];
    }

    public void ApplyVideo(IVideoChain chain)
    {
        ApplyVideoDecoder(chain, VideoDecoder);
        ApplyVideoRenderer(chain, VideoRenderer);
    }

    public void ApplyAudio(IAudioChain chain)
    {
        ApplyAudioDecoder(chain, AudioDecoder);
        ApplyAudioRenderer(chain, AudioRenderer);
    }

    public void ApplySubtitle(ISubtitleChain chain)
    {
        ApplySubtitleDecoder(chain, SubtitleDecoder);
        ApplySubtitleRenderer(chain, SubtitleRenderer);
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

    private static void ApplyAudioDecoder(IAudioChain chain, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetAudioDecoders(chain, [new FFmpegAudioDecoderFactory()]);
                break;
            case NONE:
                SetAudioDecoders(chain, [new NullAudioDecoderFactory()]);
                break;
            default:
                SetAudioDecoders(chain, [new FFmpegAudioDecoderFactory()]);
                break;
        }
    }

    private static void ApplyAudioRenderer(IAudioChain chain, string key)
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
                        SetAudioRenderers(chain, [WasapiAudioRendererFactory.For(endpoint)]);
                        return;
                    }
                }
            }

            // The device is gone; the default one is what "the output device I picked" degrades to.
        }

        switch (key)
        {
            case NONE:
                SetAudioRenderers(chain, [new NullAudioRendererFactory()]);
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

                    SetAudioRenderers(chain, [.. factories]);
                    return;
                }
        }
    }

    private static void ApplySubtitleDecoder(ISubtitleChain chain, string key)
    {
        switch (key)
        {
            case FFMPEG:
                SetSubtitleDecoders(chain, [new FFmpegSubtitleDecoderFactory()]);
                break;
            case NONE:
                SetSubtitleDecoders(chain, [new NullSubtitleDecoderFactory()]);
                break;
            default:
                SetSubtitleDecoders(chain, [new FFmpegSubtitleDecoderFactory()]);
                break;
        }
    }

    private static void ApplySubtitleRenderer(ISubtitleChain chain, string key)
    {
        switch (key)
        {
            case WIN2D:
                SetSubtitleRenderers(chain, [new Win2DSubtitleRendererFactory()]);
                break;
            case LIBASS:
                SetSubtitleRenderers(chain, [new AssSubtitleRendererFactory()]);
                break;
            case NONE:
                SetSubtitleRenderers(chain, [new NullSubtitleRendererFactory()]);
                break;
            default:
                SetSubtitleRenderers(chain, [new AssSubtitleRendererFactory(), new Win2DSubtitleRendererFactory()]);
                break;
        }
    }

    private static void SetVideoDecoders(IVideoChain chain, IVideoDecoderFactory[] factories)
    {
        chain.Components.ClearDecoders();
        foreach (IVideoDecoderFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetVideoRenderers(IVideoChain chain, IVideoRendererFactory[] factories)
    {
        chain.Components.ClearRenderers();
        foreach (IVideoRendererFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetAudioDecoders(IAudioChain chain, IAudioDecoderFactory[] factories)
    {
        chain.Components.ClearDecoders();
        foreach (IAudioDecoderFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetAudioRenderers(IAudioChain chain, IAudioRendererFactory[] factories)
    {
        chain.Components.ClearRenderers();
        foreach (IAudioRendererFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetSubtitleDecoders(ISubtitleChain chain, ISubtitleDecoderFactory[] factories)
    {
        chain.Components.ClearDecoders();
        foreach (ISubtitleDecoderFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }

    private static void SetSubtitleRenderers(ISubtitleChain chain, ISubtitleRendererFactory[] factories)
    {
        chain.Components.ClearRenderers();
        foreach (ISubtitleRendererFactory factory in factories)
        {
            chain.Components.Register(factory);
        }
    }
}
