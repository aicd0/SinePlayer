// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Abstractions.Source;
using SinePlayer.Core.Playback.Registry;

namespace SinePlayer.Core.Playback.Selection;

public sealed class AudioComponentResolver
{
    private readonly IComponentRegistry _components;

    public AudioComponentResolver() : this(new ComponentRegistry())
    {
    }

    public AudioComponentResolver(IComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _components = registry;
    }

    public AudioComponentResolver Clone()
    {
        return new AudioComponentResolver(_components.Clone());
    }

    public void Register(IAudioDecoderFactory factory)
    {
        _components.Register(factory);
    }

    public void Register(IAudioRendererFactory factory)
    {
        _components.Register(factory);
    }

    public bool Remove(IAudioDecoderFactory factory)
    {
        return _components.Remove(factory);
    }

    public bool Remove(IAudioRendererFactory factory)
    {
        return _components.Remove(factory);
    }

    public int ClearDecoders()
    {
        return _components.Clear<IAudioDecoderFactory>();
    }

    public int ClearRenderers()
    {
        return _components.Clear<IAudioRendererFactory>();
    }

    public IEnumerable<IComponentCreator<IAudioRenderer>> ResolveRenderers()
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

    public IEnumerable<IComponentCreator<IAudioDecoder>> ResolveDecoders(IMediaSource source, AudioStreamInfo stream)
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
}
