// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Playback.Surface;
using SinePlayer.Core.Playback.Hosting;

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class PlayerView(PlayerSession session) : IDisposable
{
    private readonly PlaybackInfoHandle _playbackInfo = new(session);

    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _videoRenderTarget;
    private IVideoChain? _videoChain;

    private SwapChainPanel? _subtitlePanel;
    private SwapChainPanelRenderTarget? _subtitleRenderTarget;
    private readonly List<ISubtitleChain> _subtitleChains = [];

    public UIElement VideoPanel => _videoPanel ??= new SwapChainPanel();

    /// <summary>
    /// The panel the words are drawn into, created on first use. Its opacity has to stay below 1: at 1 the two swap
    /// chain panels are composed as opaque planes, and once the picture's chain is rebound, which is what switching the
    /// video track off and on does, the plane underneath stops being composed at all, so the picture is gone from the
    /// window while its chain keeps attaching and presenting correct frames. Anything below 1 puts this surface in an
    /// alpha blended layer instead, which composes correctly, and a tenth of a percent of transparency is imperceptible.
    /// </summary>
    public UIElement SubtitlePanel => _subtitlePanel ??= new SwapChainPanel { IsHitTestVisible = false, Opacity = 0.999 };

    public PlaybackInfoHandle PlaybackInfo => _playbackInfo;

    public IVideoChain? VideoChain => _videoChain;

    public ISubtitleChain[] SubtitleChains => [.. _subtitleChains];

    public void Dispose()
    {
        DetachChains();

        _videoRenderTarget?.Dispose();
        _videoRenderTarget = null;
        _videoPanel = null;

        _subtitleRenderTarget?.Dispose();
        _subtitleRenderTarget = null;
        _subtitlePanel = null;
    }

    public SwapChainPanelRenderTarget EnsureVideoRenderTarget()
    {
        _videoRenderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)VideoPanel);
        return _videoRenderTarget;
    }

    public SwapChainPanelRenderTarget EnsureSubtitleRenderTarget()
    {
        _subtitleRenderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)SubtitlePanel);
        return _subtitleRenderTarget;
    }

    public void DetachSurfaces()
    {
        _videoRenderTarget?.DetachSurface();
        _subtitleRenderTarget?.DetachSurface();
    }

    public void AttachVideo(IVideoChain video)
    {
        _videoChain = video;

        foreach (ISubtitleChain subtitle in _subtitleChains)
        {
            subtitle.LayoutVideoChain = video;
        }

        PublishPlaybackInfo();
    }

    public void AttachSubtitle(ISubtitleChain subtitle)
    {
        if (!_subtitleChains.Contains(subtitle))
        {
            _subtitleChains.Add(subtitle);
        }

        subtitle.LayoutVideoChain = _videoChain;
        PublishPlaybackInfo();
    }

    public void DetachChains()
    {
        foreach (ISubtitleChain subtitle in _subtitleChains)
        {
            subtitle.LayoutVideoChain = null;
        }

        _videoChain = null;
        _subtitleChains.Clear();
        _playbackInfo.Detach();
    }

    private void PublishPlaybackInfo()
    {
        _playbackInfo.Attach(_videoChain, [.. _subtitleChains]);
    }
}