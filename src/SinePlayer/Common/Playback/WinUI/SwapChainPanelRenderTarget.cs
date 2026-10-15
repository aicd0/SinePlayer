// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Common.Playback.WinUI;

/// <summary>
/// Binds the engine's swap chain to a XAML panel. This is the only place that knows how the engine's
/// presentation surface reaches the UI, which is what keeps the engine free of any XAML dependency and
/// lets a video renderer be replaced without touching the page.
/// </summary>
internal sealed class SwapChainPanelRenderTarget : IVideoRenderTarget
{
    /// <summary><c>ISwapChainPanelNative</c>.</summary>
    private static readonly Guid sSwapChainPanelNativeId = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    private readonly SwapChainPanel _panel;

    private int _surfaceWidth;
    private int _surfaceHeight;
    private double _scale = 1.0;

    internal SwapChainPanelRenderTarget(SwapChainPanel panel)
    {
        _panel = panel;
        UpdateSurfaceSize();

        _panel.SizeChanged += OnPanelSizeChanged;
        _panel.CompositionScaleChanged += OnCompositionScaleChanged;
    }

    // The engine reads these from its own thread, so they are cached here — on the UI thread — rather
    // than read out of XAML across apartments.
    public int SurfaceWidth => _surfaceWidth;

    public int SurfaceHeight => _surfaceHeight;

    /// <summary>
    /// Physical pixels per logical pixel, as the panel reports it. The engine sizes the swap chain by it
    /// and cancels it again on the swap chain itself, so the buffer's pixels reach the display unchanged.
    /// </summary>
    public double RasterizationScale => _scale;

    /// <summary>
    /// A swap chain panel maps its swap chain in <em>logical</em> units and applies its composition scale
    /// when it composes, so the surface is reported in logical units and the scale alongside it: the
    /// engine multiplies the two for the buffer size and cancels the scale with a transform on the swap
    /// chain. Sizing the buffer here (in physical pixels) without that transform was the combination that
    /// once made the picture larger than the panel and cropped it to its top-left corner.
    /// <para>
    /// One scale covers both axes: the panel is not rotated or skeued, so X and Y differ only if the XAML
    /// around it applies a non-uniform transform, which nothing in this app does.
    /// </para>
    /// </summary>
    private void UpdateSurfaceSize()
    {
        _surfaceWidth = (int)Math.Round(_panel.ActualWidth);
        _surfaceHeight = (int)Math.Round(_panel.ActualHeight);

        double scale = _panel.CompositionScaleX;
        _scale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// The panel belongs to the UI thread and the engine attaches from its own, so the hand-off is
    /// marshalled across and waited for: the renderer draws into the swap chain as soon as this returns.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        DispatcherQueue? dispatcher = _panel.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        using ManualResetEventSlim completed = new(false);
        Exception? failure = null;
        if (!dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        }))
        {
            throw new InvalidOperationException("The UI thread did not accept the presentation request.");
        }

        completed.Wait();
        if (failure is not null)
        {
            throw failure;
        }
    }

    public event Action? SurfaceSizeChanged;

    public void AttachPresentationSurface(nint swapChain)
    {
        RunOnUiThread(() =>
        {
            int result = TrySetSwapChain(swapChain);
            if (result < 0)
            {
                throw Marshal.GetExceptionForHR(result) ?? new InvalidOperationException($"Unable to attach the swap chain (0x{result:X8}).");
            }
        });
    }

    /// <summary>
    /// Releases the panel's hold on the swap chain. The target stays usable: a later attach rebinds a
    /// new surface. Detaching before the engine disposes a swap chain keeps the panel from composing a
    /// surface that no longer exists.
    /// </summary>
    internal void DetachSurface()
    {
        RunOnUiThread(() => TrySetSwapChain(0));
    }

    /// <summary>Drops the target for good.</summary>
    internal void Dispose()
    {
        RunOnUiThread(() =>
        {
            _panel.SizeChanged -= OnPanelSizeChanged;
            _panel.CompositionScaleChanged -= OnCompositionScaleChanged;
            TrySetSwapChain(0);
        });
    }

    private unsafe int TrySetSwapChain(nint swapChain)
    {
        nint inspectable = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(_panel);
        try
        {
            Guid iid = sSwapChainPanelNativeId;
            int result = Marshal.QueryInterface(inspectable, in iid, out nint native);
            if (result < 0)
            {
                return result;
            }

            try
            {
                // The interface only declares SetSwapChain after the three IUnknown slots.
                nint* vtable = *(nint**)native;
                var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
                return setSwapChain(native, swapChain);
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateSurfaceSize();
        SurfaceSizeChanged?.Invoke();
    }

    /// <summary>
    /// Raised when the panel's mapping scale changes, which normally means the window moved to a
    /// display with a different scale. The surface has to be re-sized and the swap chain's transform
    /// re-applied, which the engine does from this event.
    /// </summary>
    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdateSurfaceSize();
        SurfaceSizeChanged?.Invoke();
    }
}