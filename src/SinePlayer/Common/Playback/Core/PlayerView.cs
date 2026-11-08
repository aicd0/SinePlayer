// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Common.Playback.Surface;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Pipeline;

namespace SinePlayer.Common.Playback.Core;

/// <summary>
/// One page's picture: the panel it is shown in, the render target over that panel, and the video chain that decodes
/// into it — with what that chain is asked to do and what it reports.
/// <para>
/// It is the unit a page holds, so that several pages can read and change their own picture of one playback without
/// reaching for each other's. The sound and the words are not here: they belong to the session that owns the engine,
/// because one playback has one of each.
/// </para>
/// </summary>
internal sealed class PlayerView : IDisposable
{
    private readonly PlaybackInfoHandle _playbackInfo = new();

    private SwapChainPanel? _panel;
    private SwapChainPanelRenderTarget? _renderTarget;
    private IVideoChain? _chain;
    private VideoTransform _transform = VideoTransform.Identity;

    /// <summary>The panel the page puts in its window, created on first use.</summary>
    public UIElement Panel => _panel ??= new SwapChainPanel();

    /// <summary>What this view reports about the picture it is playing, and the sound and words it shares.</summary>
    public PlaybackInfoHandle PlaybackInfo => _playbackInfo;

    public IVideoChain? Chain => _chain;

    /// <summary>
    /// How the picture is fitted and turned. Kept here rather than on the chain so that a view created before the
    /// pipeline exists still remembers what it was asked for, and applies it when the chain appears.
    /// </summary>
    public VideoTransform Transform
    {
        get => _transform;
        set
        {
            if (_transform == value)
            {
                return;
            }

            _transform = value;
            if (_chain is { } chain)
            {
                chain.Transform = value;
            }
        }
    }

    /// <summary>The render target over this view's panel, created on first use, for the chain this view is given.</summary>
    public SwapChainPanelRenderTarget EnsureRenderTarget()
    {
        _renderTarget ??= new SwapChainPanelRenderTarget((SwapChainPanel)Panel);
        return _renderTarget;
    }

    /// <summary>
    /// Ties this view to what the pipeline built for it. The transform it was asked for is applied here, because this
    /// is the first moment there is a chain to apply it to.
    /// </summary>
    public void Attach(PlaybackPipeline pipeline, IVideoChain chain, IAudioChain? audio, ISubtitleChain? subtitle)
    {
        _chain = chain;
        chain.Transform = _transform;
        _playbackInfo.Attach(pipeline, chain, audio, subtitle);
    }

    public void Detach()
    {
        _chain = null;
        _playbackInfo.Detach();
    }

    public void Dispose()
    {
        Detach();

        _renderTarget?.DetachSurface();
        _renderTarget?.Dispose();
        _renderTarget = null;
        _panel = null;
    }
}
