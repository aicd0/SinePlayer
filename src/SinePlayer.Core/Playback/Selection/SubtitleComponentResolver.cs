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

public sealed class SubtitleComponentResolver
{
    private readonly IComponentRegistry _components;
    private PlaybackPreferences _preferences = PlaybackPreferences.Default;

    public SubtitleComponentResolver() : this(new ComponentRegistry())
    {
    }

    public SubtitleComponentResolver(IComponentRegistry registry)
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

    public SubtitleComponentResolver Clone()
    {
        return new SubtitleComponentResolver(_components.Clone()) { Preferences = Preferences };
    }

    public void Register(ISubtitleDecoderFactory factory)
    {
        _components.Register(factory);
    }

    public void Register(ISubtitleRendererFactory factory)
    {
        _components.Register(factory);
    }

    public bool Remove(ISubtitleDecoderFactory factory)
    {
        return _components.Remove(factory);
    }

    public bool Remove(ISubtitleRendererFactory factory)
    {
        return _components.Remove(factory);
    }

    public int ClearDecoders()
    {
        return _components.Clear<ISubtitleDecoderFactory>();
    }

    public int ClearRenderers()
    {
        return _components.Clear<ISubtitleRendererFactory>();
    }

    public IEnumerable<IComponentCreator<ISubtitleRenderer>> ResolveRenderers(SubtitleStreamInfo stream, SubtitleCueFormat format)
    {
        foreach (ISubtitleRendererFactory factory in _components.GetComponents<ISubtitleRendererFactory>())
        {
            OperationResult<IComponentCreator<ISubtitleRenderer>> offered = factory.Create(stream, format);
            if (offered.IsSuccessful)
            {
                yield return offered.Result;
            }
        }
    }

    public IEnumerable<IComponentCreator<ISubtitleDecoder>> ResolveDecoders(IMediaSource source, SubtitleStreamInfo stream)
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
}
