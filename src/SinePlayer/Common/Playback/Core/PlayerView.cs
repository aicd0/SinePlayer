// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Playback.Surface;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

internal sealed partial class PlayerView(PlayerSession session) : IDisposable
{
    private readonly PlaybackInfoHandle _playbackInfo = new(session);

    private SwapChainPanel? _videoPanel;
    private SwapChainPanelRenderTarget? _videoRenderTarget;
    private IVideoChain? _videoChain;
    private VideoTransform _videoTransform = VideoTransform.Identity;

    private SwapChainPanel? _subtitlePanel;
    private SwapChainPanelRenderTarget? _subtitleRenderTarget;
    private ISubtitleChain? _subtitleChain;
    private SubtitlePresentation _subtitlePresentation = SubtitlePresentation.Identity;

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

    public ISubtitleChain? SubtitleChain => _subtitleChain;

    public VideoTransform VideoTransform
    {
        get => _videoTransform;
        set
        {
            if (_videoTransform == value)
            {
                return;
            }

            _videoTransform = value;

            if (_videoChain is { } chain)
            {
                chain.Transform = value;
            }
        }
    }

    public SubtitlePresentation SubtitlePresentation
    {
        get => _subtitlePresentation;
        set
        {
            if (_subtitlePresentation == value)
            {
                return;
            }

            _subtitlePresentation = value;

            if (_subtitleChain is { } chain)
            {
                chain.Presentation = value;
            }
        }
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

    public void AttachVideo(IVideoChain video)
    {
        _videoChain = video;
        video.Transform = _videoTransform;

        if (_subtitleChain is { } subtitle)
        {
            subtitle.LayoutVideoChain = video;
        }

        _playbackInfo.Attach(_videoChain, _subtitleChain);
    }

    public void AttachSubtitle(ISubtitleChain subtitle)
    {
        _subtitleChain = subtitle;
        subtitle.Presentation = _subtitlePresentation;
        subtitle.LayoutVideoChain = _videoChain;
        _playbackInfo.Attach(_videoChain, _subtitleChain);
    }

    public void Detach()
    {
        if (_subtitleChain is { } subtitle)
        {
            subtitle.LayoutVideoChain = null;
        }

        _videoChain = null;
        _subtitleChain = null;
        _playbackInfo.Detach();
    }

    public void Dispose()
    {
        Detach();

        _videoRenderTarget?.DetachSurface();
        _videoRenderTarget?.Dispose();
        _videoRenderTarget = null;
        _videoPanel = null;

        _subtitleRenderTarget?.DetachSurface();
        _subtitleRenderTarget?.Dispose();
        _subtitleRenderTarget = null;
        _subtitlePanel = null;
    }
}