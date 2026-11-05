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
    private enum ChainChanges
    {
        None = 0,
        Source = 1,
        VideoRenderer = 2,
        VideoDecoder = 4,
        AudioRenderer = 8,
        AudioDecoder = 16,
        SubtitleRenderer = 32,
        SubtitleDecoder = 64,
    }

    private ChainChanges DiffSource(MediaSourceHolder sourceHolder)
    {
        MediaSourceHolder? current = _source.Latest?.Component;
        if (current is null)
        {
            return ChainChanges.Source
                | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer
                | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer
                | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        static bool SameStream(MediaSource? beforeSource, MediaStreamInfo? before, MediaSource? afterSource, MediaStreamInfo? after)
        {
            return beforeSource == afterSource && before == after;
        }

        ChainChanges changes = ChainChanges.None;

        if (!SameStream(current.VideoSource, current.VideoStream, sourceHolder.VideoSource, sourceHolder.VideoStream))
        {
            changes |= ChainChanges.Source | ChainChanges.VideoDecoder | ChainChanges.VideoRenderer;
        }

        if (!SameStream(current.AudioSource, current.AudioStream, sourceHolder.AudioSource, sourceHolder.AudioStream))
        {
            changes |= ChainChanges.Source | ChainChanges.AudioDecoder | ChainChanges.AudioRenderer;
        }

        if (!SameStream(current.SubtitleSource, current.SubtitleStream, sourceHolder.SubtitleSource, sourceHolder.SubtitleStream))
        {
            changes |= ChainChanges.Source | ChainChanges.SubtitleDecoder | ChainChanges.SubtitleRenderer;
        }

        return changes;
    }

    private OperationResult<ChainChanges> BuildVideoChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<IVideoRenderer>? renderer = null;
        PipelineComponent<IVideoDecoder>? decoder = null;
        try
        {
            if (sourceHolder.VideoSource is { } videoSource && sourceHolder.VideoStream is { } videoStream)
            {
                IVideoRenderTarget? videoTarget = _video.Target;
                if (videoTarget is null)
                {
                    return result.Failure("The file has video but no presentation surface was supplied.");
                }

                // Resolve video renderer
                bool rebuildVideoRenderer = sourceChanges.HasFlag(ChainChanges.VideoRenderer);
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

                    changed |= ChainChanges.VideoRenderer;
                }

                // Resolve video decoder
                bool rebuildVideoDecoder = renderer is not null || sourceChanges.HasFlag(ChainChanges.VideoDecoder);
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
                    changed |= ChainChanges.VideoDecoder;
                }
            }
            else
            {
                if (_video.Decoder.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.VideoDecoder;
                }

                if (_video.Renderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.VideoRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.VideoDecoder) || changed.HasFlag(ChainChanges.VideoRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Video);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                    _video.Binding = new(newSourceHolder.VideoSource, newSourceHolder.VideoStream);
                }

                if (changed.HasFlag(ChainChanges.VideoDecoder))
                {
                    _video.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.VideoRenderer))
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

    private OperationResult<ChainChanges> BuildAudioChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<IAudioRenderer>? renderer = null;
        PipelineComponent<IAudioDecoder>? decoder = null;
        AudioStreamInfo? negotiatedFormat = null;
        try
        {
            if (sourceHolder.AudioSource is { } audioSource && sourceHolder.AudioStream is { } audioStream)
            {
                // Resolve audio renderer
                bool rebuildAudioRenderer = sourceChanges.HasFlag(ChainChanges.AudioRenderer);
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
                    changed |= ChainChanges.AudioRenderer;
                }

                // Resolve audio decoder
                bool rebuildAudioDecoder = sourceChanges.HasFlag(ChainChanges.AudioDecoder);
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
                    changed |= ChainChanges.AudioDecoder;
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
                    changed |= sourceChanges & ChainChanges.AudioDecoder;
                }

                if (_audio.Renderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.AudioRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.AudioDecoder) || changed.HasFlag(ChainChanges.AudioRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Audio);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                }

                if (changed.HasFlag(ChainChanges.AudioDecoder))
                {
                    _audio.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.AudioRenderer))
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

    private OperationResult<ChainChanges> BuildSubtitleChain(
        ComponentResolver resolver,
        MediaSourceHolder sourceHolder,
        ChainChanges sourceChanges)
    {
        OperationResultCreator<ChainChanges> result = new();

        ChainChanges changed = sourceChanges & ChainChanges.Source;
        PipelineComponent<ISubtitleDecoder>? decoder = null;
        PipelineComponent<ISubtitleRenderer>? renderer = null;
        try
        {
            if (sourceHolder.SubtitleSource is { } subtitleSource && sourceHolder.SubtitleStream is { } subtitleStream)
            {
                IVideoRenderTarget? subtitleTarget = _subtitle.Target;
                if (subtitleTarget is null)
                {
                    return result.Failure("The file has a subtitle track but no subtitle surface was supplied.");
                }

                // Resolve subtitle decoder
                bool rebuildSubtitleDecoder = sourceChanges.HasFlag(ChainChanges.SubtitleDecoder);
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
                    changed |= ChainChanges.SubtitleDecoder;
                }

                // Resolve subtitle renderer
                PipelineComponent<ISubtitleDecoder>? runningDecoder = decoder ?? latestDecoder;
                if (runningDecoder is null)
                {
                    return result.Failure("The track has no decoder to say what its cues are made of.");
                }

                SubtitleCueFormat format = runningDecoder.Component.CueFormat;
                bool rebuildSubtitleRenderer = decoder is not null || sourceChanges.HasFlag(ChainChanges.SubtitleRenderer);
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

                    changed |= ChainChanges.SubtitleRenderer;
                }
            }
            else
            {
                if (_subtitle.Decoder.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.SubtitleDecoder;
                }

                if (_subtitle.Renderer.HasComponent)
                {
                    changed |= sourceChanges & ChainChanges.SubtitleRenderer;
                }
            }

            if (changed.HasFlag(ChainChanges.SubtitleDecoder) || changed.HasFlag(ChainChanges.SubtitleRenderer))
            {
                PipelineVersion versions = BumpVersions(changed);

                if (changed.HasFlag(ChainChanges.Source))
                {
                    MediaSourceHolder newSourceHolder = CreateSourceHolderFromChange(sourceHolder, MediaStreamKind.Subtitle);
                    _source.Publish(new(newSourceHolder, "source"), versions);
                }

                if (changed.HasFlag(ChainChanges.SubtitleDecoder))
                {
                    _subtitle.Decoder.Publish(decoder, versions);
                    decoder = null;
                }

                if (changed.HasFlag(ChainChanges.SubtitleRenderer))
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

    private MediaSourceHolder CreateSourceHolderFromSelection()
    {
        MediaSourceHolder selection = _selection;
        VideoStreamInfo? videoStream = selection.VideoStream;
        AudioStreamInfo? audioStream = selection.AudioStream;
        SubtitleStreamInfo? subtitleStream = selection.SubtitleStream;
        MediaSource?[] sources = [selection.VideoSource, selection.AudioSource, selection.SubtitleSource];
        return new([.. sources.OfType<MediaSource>().Distinct()], videoStream, audioStream, subtitleStream);
    }

    private MediaSourceHolder CreateSourceHolderFromChange(MediaSourceHolder desired, MediaStreamKind kind)
    {
        MediaSourceHolder? previous = _source.Latest?.Component;

        VideoStreamInfo? videoStream = kind == MediaStreamKind.Video ? desired.VideoStream : previous?.VideoStream;
        AudioStreamInfo? audioStream = kind == MediaStreamKind.Audio ? desired.AudioStream : previous?.AudioStream;
        SubtitleStreamInfo? subtitleStream = kind == MediaStreamKind.Subtitle ? desired.SubtitleStream : previous?.SubtitleStream;

        MediaSource? videoSource = kind == MediaStreamKind.Video ? desired.VideoSource : previous?.VideoSource;
        MediaSource? audioSource = kind == MediaStreamKind.Audio ? desired.AudioSource : previous?.AudioSource;
        MediaSource? subtitleSource = kind == MediaStreamKind.Subtitle ? desired.SubtitleSource : previous?.SubtitleSource;

        MediaSource?[] sources = [videoSource, audioSource, subtitleSource];
        return new([.. sources.OfType<MediaSource>().Distinct()], videoStream, audioStream, subtitleStream);
    }

    private PipelineVersion BumpVersions(ChainChanges changed)
    {
        PipelineVersion current = CurrentVersions();
        long nextVersion = current.Next;
        return new()
        {
            Source = changed.HasFlag(ChainChanges.Source) ? nextVersion : current.Source,
            VideoDecoder = changed.HasFlag(ChainChanges.VideoDecoder) ? nextVersion : current.VideoDecoder,
            VideoRenderer = changed.HasFlag(ChainChanges.VideoRenderer) ? nextVersion : current.VideoRenderer,
            AudioDecoder = changed.HasFlag(ChainChanges.AudioDecoder) ? nextVersion : current.AudioDecoder,
            AudioRenderer = changed.HasFlag(ChainChanges.AudioRenderer) ? nextVersion : current.AudioRenderer,
            SubtitleDecoder = changed.HasFlag(ChainChanges.SubtitleDecoder) ? nextVersion : current.SubtitleDecoder,
            SubtitleRenderer = changed.HasFlag(ChainChanges.SubtitleRenderer) ? nextVersion : current.SubtitleRenderer,
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
