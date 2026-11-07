// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using SinePlayer.Core.Playback.Abstractions.Registry;
using SinePlayer.Core.Playback.Registry;

namespace SinePlayer.Core.Playback.Selection;

public sealed class ComponentResolver
{
    private readonly IComponentRegistry _components;

    public ComponentResolver() : this(new ComponentRegistry())
    {
    }

    public ComponentResolver(IComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _components = registry;
        Video = new VideoComponentResolver(registry);
        Audio = new AudioComponentResolver(registry);
        Subtitle = new SubtitleComponentResolver(registry);
    }

    public VideoComponentResolver Video { get; }

    public AudioComponentResolver Audio { get; }

    public SubtitleComponentResolver Subtitle { get; }

    public PlaybackPreferences Preferences
    {
        get => Video.Preferences;
        set
        {
            Video.Preferences = value;
            Audio.Preferences = value;
            Subtitle.Preferences = value;
        }
    }

    public ComponentResolver Clone()
    {
        return new ComponentResolver(_components.Clone());
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
}
