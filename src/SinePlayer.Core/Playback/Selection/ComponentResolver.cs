// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Registry;

namespace SinePlayer.Core.Playback.Selection;

public sealed class ComponentResolver
{
    private readonly IComponentRegistry _components;
    private PlaybackPreferences _preferences = PlaybackPreferences.Default;

    public ComponentResolver() : this(new ComponentRegistry())
    {
    }

    public ComponentResolver(IComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _components = registry;
    }

    public PlaybackPreferences Preferences
    {
        get => Volatile.Read(ref _preferences);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _preferences, value);
        }
    }

    public ComponentResolver Clone()
    {
        return new ComponentResolver(_components.Clone()) { Preferences = Preferences };
    }

    public IReadOnlyList<TFactory> GetComponents<TFactory>() where TFactory : class, IComponentFactory
    {
        return _components.GetComponents<TFactory>();
    }

    public void Register(IComponentFactory factory)
    {
        _components.Register(factory);
    }

    public bool Remove(IComponentFactory factory)
    {
        return _components.Remove(factory);
    }

    public int Clear<TFactory>() where TFactory : class, IComponentFactory
    {
        return _components.Clear<TFactory>();
    }

    #region Resolution

    public IEnumerable<IComponentCreator<IVideoRenderer>> ResolveVideoRenderers()
    {
        foreach (IVideoRendererFactory factory in _components.GetComponents<IVideoRendererFactory>())
        {
            OperationResult<IComponentCreator<IVideoRenderer>> offered = factory.Create();
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    public IEnumerable<IComponentCreator<IVideoDecoder>> ResolveVideoDecoders(IMediaSource source, VideoStreamInfo stream, IGpuDevice? gpuDevice, int bufferedFrames)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferedFrames);

        return EnumerateVideoDecoders(source, stream, gpuDevice, bufferedFrames, Preferences.PreferHardwareDecoding);
    }

    public IEnumerable<IComponentCreator<IAudioRenderer>> ResolveAudioRenderers()
    {
        foreach (IAudioRendererFactory factory in _components.GetComponents<IAudioRendererFactory>())
        {
            OperationResult<IComponentCreator<IAudioRenderer>> offered = factory.Create();
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    public IEnumerable<IComponentCreator<IAudioDecoder>> ResolveAudioDecoders(IMediaSource source, AudioStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);

        return EnumerateAudioDecoders(source, stream);
    }

    public IEnumerable<IComponentCreator<ISubtitleRenderer>> ResolveSubtitleRenderers(SubtitleCueFormat format)
    {
        foreach (ISubtitleRendererFactory factory in _components.GetComponents<ISubtitleRendererFactory>())
        {
            OperationResult<IComponentCreator<ISubtitleRenderer>> offered = factory.Create(format);
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    public IEnumerable<IComponentCreator<ISubtitleDecoder>> ResolveSubtitleDecoders(IMediaSource? source, SubtitleStreamInfo stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        foreach (ISubtitleDecoderFactory factory in _components.GetComponents<ISubtitleDecoderFactory>())
        {
            OperationResult<IComponentCreator<ISubtitleDecoder>> offered = factory.Create(source, stream);
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    private IEnumerable<IComponentCreator<IAudioDecoder>> EnumerateAudioDecoders(IMediaSource source, AudioStreamInfo stream)
    {
        foreach (IAudioDecoderFactory factory in _components.GetComponents<IAudioDecoderFactory>())
        {
            OperationResult<IComponentCreator<IAudioDecoder>> offered = factory.Create(source, stream, DecoderOptions.Default);
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    private IEnumerable<IComponentCreator<IVideoDecoder>> EnumerateVideoDecoders(IMediaSource source, VideoStreamInfo stream, IGpuDevice? gpuDevice, int bufferedFrames, bool hardwareDecoding)
    {
        DecoderOptions options = new()
        {
            HardwareDecoding = hardwareDecoding,
            GpuDevice = gpuDevice,
            BufferedFrames = bufferedFrames,
        };

        foreach (IVideoDecoderFactory factory in _components.GetComponents<IVideoDecoderFactory>())
        {
            OperationResult<IComponentCreator<IVideoDecoder>> offered = factory.Create(source, stream, options);
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }

        if (hardwareDecoding)
        {
            foreach (IComponentCreator<IVideoDecoder> creator in EnumerateVideoDecoders(source, stream, gpuDevice, bufferedFrames, hardwareDecoding: false))
            {
                yield return creator;
            }
        }
    }

    #endregion
}
