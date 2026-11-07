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

public sealed class VideoComponentResolver
{
    private readonly IComponentRegistry _components;
    private VideoPreferences _preferences = VideoPreferences.Default;

    public VideoComponentResolver() : this(new ComponentRegistry())
    {
    }

    public VideoComponentResolver(IComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _components = registry;
    }

    public VideoPreferences Preferences
    {
        get => Volatile.Read(ref _preferences);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _preferences, value);
        }
    }

    public VideoComponentResolver Clone()
    {
        return new VideoComponentResolver(_components.Clone()) { Preferences = Preferences };
    }

    public void Register(IVideoDecoderFactory factory)
    {
        _components.Register(factory);
    }

    public void Register(IVideoRendererFactory factory)
    {
        _components.Register(factory);
    }

    public bool Remove(IVideoDecoderFactory factory)
    {
        return _components.Remove(factory);
    }

    public bool Remove(IVideoRendererFactory factory)
    {
        return _components.Remove(factory);
    }

    public int ClearDecoders()
    {
        return _components.Clear<IVideoDecoderFactory>();
    }

    public int ClearRenderers()
    {
        return _components.Clear<IVideoRendererFactory>();
    }

    public IEnumerable<IComponentCreator<IVideoRenderer>> ResolveRenderers()
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

    public IEnumerable<IComponentCreator<IVideoDecoder>> ResolveDecoders(IMediaSource source, VideoStreamInfo stream, IGpuDevice? gpuDevice, int bufferedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferedFrames);

        return EnumerateDecoders(source, stream, gpuDevice, bufferedFrames, Preferences.PreferHardwareDecoding);
    }

    private IEnumerable<IComponentCreator<IVideoDecoder>> EnumerateDecoders(IMediaSource source, VideoStreamInfo stream, IGpuDevice? gpuDevice, int bufferedFrames, bool hardwareDecoding)
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
            foreach (IComponentCreator<IVideoDecoder> creator in EnumerateDecoders(source, stream, gpuDevice, bufferedFrames, hardwareDecoding: false))
            {
                yield return creator;
            }
        }
    }
}
