// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Selection;

namespace SinePlayer.Core.Playback.Pipeline;

public sealed partial class PlaybackPipeline
{
    #region Components

    [Flags]
    private enum ChainChange
    {
        None = 0,
        Source = 1,
        Decoder = 2,
        Renderer = 4,
    }

    private OperationResult<ChainChange> BuildVideoChain(ComponentResolver resolver)
    {
        OperationResultCreator<ChainChange> result = new();

        StreamBinding<VideoStreamInfo> selected = _video.Selected;
        bool sourceChanged = !_video.Binding.Matches(selected.Source, selected.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<IVideoRenderer>? renderer = null;
        PipelineComponent<IVideoDecoder>? decoder = null;
        try
        {
            if (selected.Source is { } videoSource && selected.Stream is { } videoStream)
            {
                IVideoRenderTarget? videoTarget = _video.Target;
                if (videoTarget is null)
                {
                    return result.Failure("The file has video but no presentation surface was supplied.");
                }

                // Resolve video renderer
                bool rebuildVideoRenderer = sourceChanged;
                PipelineComponent<IVideoRenderer>? latestRenderer = _video.Renderer.Latest;
                OperationResult<PipelineComponent<IVideoRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveVideoRenderers(),
                    rebuildVideoRenderer ? null : latestRenderer?.Signature,
                    "video renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult configured = renderer.Component.Configure(videoStream, videoTarget);
                    if (!configured.IsSuccessful)
                    {
                        return result.Failure(configured.Message);
                    }

                    changed |= ChainChange.Renderer;
                }

                // Resolve video decoder
                bool rebuildVideoDecoder = renderer is not null || sourceChanged;
                PipelineComponent<IVideoDecoder>? latestDecoder = _video.Decoder.Latest;
                IGpuDevice? gpuDevice = (renderer ?? latestRenderer)?.Component?.GpuDevice;
                OperationResult<PipelineComponent<IVideoDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveVideoDecoders(videoSource.Source, videoStream, gpuDevice, VideoChain.DecoderBufferedFrames),
                    rebuildVideoDecoder ? null : latestDecoder?.Signature,
                    "video decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
                }
            }
            else
            {
                if (_video.Decoder.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Decoder : ChainChange.None;
                }

                if (_video.Renderer.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Renderer : ChainChange.None;
                }
            }

            if (changed.HasFlag(ChainChange.Decoder) || changed.HasFlag(ChainChange.Renderer))
            {
                PipelineVersion versions = BumpVersions(changed, MediaStreamKind.Video);

                if (changed.HasFlag(ChainChange.Source))
                {
                    _video.Binding = _video.Selected;
                }

                if (changed.HasFlag(ChainChange.Decoder))
                {
                    _video.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChange.Renderer))
                {
                    _video.Renderer.Publish(renderer, versions);
                    renderer = null;
                }
            }

            return result.Success(changed);
        }
        finally
        {
            renderer?.Dispose();
            decoder?.Dispose();
        }
    }

    private OperationResult<ChainChange> BuildAudioChain(ComponentResolver resolver)
    {
        OperationResultCreator<ChainChange> result = new();

        StreamBinding<AudioStreamInfo> selected = _audio.Selected;
        bool sourceChanged = !_audio.Binding.Matches(selected.Source, selected.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<IAudioRenderer>? renderer = null;
        PipelineComponent<IAudioDecoder>? decoder = null;
        AudioStreamInfo? negotiatedFormat = null;
        try
        {
            if (selected.Source is { } audioSource && selected.Stream is { } audioStream)
            {
                // Resolve audio renderer
                bool rebuildAudioRenderer = sourceChanged;
                PipelineComponent<IAudioRenderer>? latestRenderer = _audio.Renderer.Latest;
                OperationResult<PipelineComponent<IAudioRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveAudioRenderers(),
                    rebuildAudioRenderer ? null : latestRenderer?.Signature,
                    "audio renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult<AudioStreamInfo> negotiated = renderer.Component.Negotiate(audioStream);
                    if (!negotiated.IsSuccessful)
                    {
                        return result.Failure(negotiated.Message);
                    }

                    negotiatedFormat = negotiated.Result;
                    changed |= ChainChange.Renderer;
                }

                // Resolve audio decoder
                bool rebuildAudioDecoder = sourceChanged;
                PipelineComponent<IAudioDecoder>? latestDecoder = _audio.Decoder.Latest;
                OperationResult<PipelineComponent<IAudioDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveAudioDecoders(audioSource.Source, audioStream),
                    rebuildAudioDecoder ? null : latestDecoder?.Signature,
                    "audio decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
                }

                if (negotiatedFormat is not null)
                {
                    _audio.Filters.Configure(audioStream, negotiatedFormat);
                }
            }
            else
            {
                if (_audio.Decoder.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Decoder : ChainChange.None;
                }

                if (_audio.Renderer.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Renderer : ChainChange.None;
                }
            }

            if (changed.HasFlag(ChainChange.Decoder) || changed.HasFlag(ChainChange.Renderer))
            {
                PipelineVersion versions = BumpVersions(changed, MediaStreamKind.Audio);

                if (changed.HasFlag(ChainChange.Source))
                {
                    _audio.Binding = _audio.Selected;
                }

                if (changed.HasFlag(ChainChange.Decoder))
                {
                    _audio.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChange.Renderer))
                {
                    _audio.Renderer.Publish(renderer, versions);
                    renderer = null;
                }
            }

            return result.Success(changed);
        }
        finally
        {
            renderer?.Dispose();
            decoder?.Dispose();
        }
    }

    private OperationResult<ChainChange> BuildSubtitleChain(ComponentResolver resolver)
    {
        OperationResultCreator<ChainChange> result = new();

        StreamBinding<SubtitleStreamInfo> selected = _subtitle.Selected;
        bool sourceChanged = !_subtitle.Binding.Matches(selected.Source, selected.Stream);
        ChainChange changed = sourceChanged ? ChainChange.Source : ChainChange.None;
        PipelineComponent<ISubtitleDecoder>? decoder = null;
        PipelineComponent<ISubtitleRenderer>? renderer = null;
        try
        {
            if (selected.Source is { } subtitleSource && selected.Stream is { } subtitleStream)
            {
                IVideoRenderTarget? subtitleTarget = _subtitle.Target;
                if (subtitleTarget is null)
                {
                    return result.Failure("The file has a subtitle track but no subtitle surface was supplied.");
                }

                // Resolve subtitle decoder
                bool rebuildSubtitleDecoder = sourceChanged;
                PipelineComponent<ISubtitleDecoder>? latestDecoder = _subtitle.Decoder.Latest;
                OperationResult<PipelineComponent<ISubtitleDecoder>?> decoderResolved = ResolveComponent(
                    resolver.ResolveSubtitleDecoders(subtitleSource.Source, subtitleStream),
                    rebuildSubtitleDecoder ? null : latestDecoder?.Signature,
                    "subtitle decoder");
                if (!decoderResolved.IsSuccessful)
                {
                    return result.Failure(decoderResolved.Message);
                }

                decoder = decoderResolved.Result;
                if (decoder is not null)
                {
                    changed |= ChainChange.Decoder;
                }

                // Resolve subtitle renderer
                PipelineComponent<ISubtitleDecoder>? runningDecoder = decoder ?? latestDecoder;
                if (runningDecoder is null)
                {
                    return result.Failure("The track has no decoder to say what its cues are made of.");
                }

                SubtitleCueFormat format = runningDecoder.Component.CueFormat;
                bool rebuildSubtitleRenderer = decoder is not null || sourceChanged;
                PipelineComponent<ISubtitleRenderer>? latestRenderer = _subtitle.Renderer.Latest;
                OperationResult<PipelineComponent<ISubtitleRenderer>?> rendererResolved = ResolveComponent(
                    resolver.ResolveSubtitleRenderers(subtitleStream, format),
                    rebuildSubtitleRenderer ? null : latestRenderer?.Signature,
                    "subtitle renderer");
                if (!rendererResolved.IsSuccessful)
                {
                    return result.Failure(rendererResolved.Message);
                }

                renderer = rendererResolved.Result;
                if (renderer is not null)
                {
                    OperationResult configured = renderer.Component.Configure(subtitleStream, subtitleSource.Info.Attachments, subtitleTarget);
                    if (!configured.IsSuccessful)
                    {
                        return result.Failure(configured.Message);
                    }

                    changed |= ChainChange.Renderer;
                }
            }
            else
            {
                if (_subtitle.Decoder.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Decoder : ChainChange.None;
                }

                if (_subtitle.Renderer.HasComponent)
                {
                    changed |= sourceChanged ? ChainChange.Renderer : ChainChange.None;
                }
            }

            if (changed.HasFlag(ChainChange.Decoder) || changed.HasFlag(ChainChange.Renderer))
            {
                PipelineVersion versions = BumpVersions(changed, MediaStreamKind.Subtitle);

                if (changed.HasFlag(ChainChange.Source))
                {
                    _subtitle.Binding = _subtitle.Selected;
                }

                if (changed.HasFlag(ChainChange.Decoder))
                {
                    _subtitle.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChange.Renderer))
                {
                    _subtitle.Renderer.Publish(renderer, versions);
                    renderer = null;
                }
            }

            return result.Success(changed);
        }
        finally
        {
            decoder?.Dispose();
            renderer?.Dispose();
        }
    }

    private static OperationResult<PipelineComponent<T>?> ResolveComponent<T>(
        IEnumerable<IComponentCreator<T>> creators,
        string? runningSignature,
        string role) where T : class, IDisposable
    {
        OperationResultCreator<PipelineComponent<T>?> result = new();
        string error = $"No {role} could be offered.";

        foreach (IComponentCreator<T> creator in creators)
        {
            if (creator.Signature == runningSignature)
            {
                return result.Success(null);
            }

            OperationResult created = creator.Create(out T? made);
            if (created.IsSuccessful && made is not null)
            {
                return result.Success(new(made, creator.Signature));
            }

            error = created.IsSuccessful ? $"{creator.Signature} made no {role}." : created.Message;
        }

        return result.Failure(error);
    }

    private MediaSourceHolder CreateSourceHolder()
    {
        MediaSource?[] sources = [_video.Binding.Source, _audio.Binding.Source, _subtitle.Binding.Source];
        return new([.. sources.OfType<MediaSource>().Distinct()]);
    }

    private PipelineVersion BumpVersions(ChainChange changed, MediaStreamKind kind)
    {
        PipelineVersion current = CurrentVersions();
        long next = current.Next;
        bool source = changed.HasFlag(ChainChange.Source);
        bool decoder = changed.HasFlag(ChainChange.Decoder);
        bool renderer = changed.HasFlag(ChainChange.Renderer);
        return kind switch
        {
            MediaStreamKind.Video => current with
            {
                Source = source ? next : current.Source,
                VideoDecoder = decoder ? next : current.VideoDecoder,
                VideoRenderer = renderer ? next : current.VideoRenderer,
            },
            MediaStreamKind.Audio => current with
            {
                Source = source ? next : current.Source,
                AudioDecoder = decoder ? next : current.AudioDecoder,
                AudioRenderer = renderer ? next : current.AudioRenderer,
            },
            _ => current with
            {
                Source = source ? next : current.Source,
                SubtitleDecoder = decoder ? next : current.SubtitleDecoder,
                SubtitleRenderer = renderer ? next : current.SubtitleRenderer,
            },
        };
    }

    private PipelineVersion CurrentVersions()
    {
        PipelineVersion versions = default;
        versions = PipelineVersion.Max(versions, _source.HighestVersions);
        versions = PipelineVersion.Max(versions, _video.Decoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _audio.Decoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _video.Renderer.HighestVersions);
        versions = PipelineVersion.Max(versions, _audio.Renderer.HighestVersions);
        versions = PipelineVersion.Max(versions, _subtitle.Decoder.HighestVersions);
        versions = PipelineVersion.Max(versions, _subtitle.Renderer.HighestVersions);
        return versions;
    }

    #endregion
}
