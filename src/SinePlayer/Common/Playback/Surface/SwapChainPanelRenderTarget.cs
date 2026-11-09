// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using SinePlayer.Core.Common.DebugTools;
using SinePlayer.Core.Common.Threading;
using SinePlayer.Core.Playback.Abstractions.Render;

namespace SinePlayer.Common.Playback.Surface;

internal sealed class SwapChainPanelRenderTarget : IVideoRenderTarget
{
    private const string TAG = nameof(SwapChainPanelRenderTarget);

    /// <summary><c>ISwapChainPanelNative</c>.</summary>
    private static readonly Guid sSwapChainPanelNativeId = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    private readonly SwapChainPanel _panel;

    private int _surfaceWidth;
    private int _surfaceHeight;
    private double _scale = 1.0;

    private bool _panelLoaded;
    private bool _swapChainAttached;

    internal SwapChainPanelRenderTarget(SwapChainPanel panel)
    {
        _panel = panel;
        _panelLoaded = _panel.IsLoaded;
        UpdateSurfaceSize();

        _panel.Loaded += Panel_LoadedOrUnloaded;
        _panel.Unloaded += Panel_LoadedOrUnloaded;
        _panel.SizeChanged += Panel_SizeChanged;
        _panel.CompositionScaleChanged += Panel_CompositionScaleChanged;
    }

    public event Action? SurfaceSizeChanged;

    public int SurfaceWidth => _surfaceWidth;

    public int SurfaceHeight => _surfaceHeight;

    public double RasterizationScale => _scale;

    internal void Dispose()
    {
        RunOnUiThread(() =>
        {
            _panel.Loaded -= Panel_LoadedOrUnloaded;
            _panel.Unloaded -= Panel_LoadedOrUnloaded;
            _panel.SizeChanged -= Panel_SizeChanged;
            _panel.CompositionScaleChanged -= Panel_CompositionScaleChanged;
            DetachCore();
        });
    }

    public void AttachPresentationSurface(nint swapChain)
    {
        RunOnUiThread(() =>
        {
            int result = TrySetSwapChain(swapChain);
            if (result < 0)
            {
                throw Marshal.GetExceptionForHR(result) ?? new InvalidOperationException($"Unable to attach the swap chain (0x{result:X8}).");
            }

            _swapChainAttached = true;
        });
    }

    internal void DetachSurface()
    {
        RunOnUiThread(DetachCore);
    }

    private void DetachCore()
    {
        if (!_swapChainAttached)
        {
            return;
        }

        _swapChainAttached = false;

        if (!_panelLoaded)
        {
            return;
        }

        try
        {
            TrySetSwapChain(0);
        }
        catch (Exception ex)
        {
            Logger.F(TAG, "The swap chain could not be detached.", ex);
        }
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

    private void Panel_LoadedOrUnloaded(object sender, RoutedEventArgs args)
    {
        _panelLoaded = _panel.IsLoaded;
    }

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateSurfaceSize();
        SurfaceSizeChanged?.Invoke();
    }

    private void Panel_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdateSurfaceSize();
        SurfaceSizeChanged?.Invoke();
    }

    private void UpdateSurfaceSize()
    {
        _surfaceWidth = (int)Math.Round(_panel.ActualWidth);
        _surfaceHeight = (int)Math.Round(_panel.ActualHeight);

        double scale = _panel.CompositionScaleX;
        _scale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }

    private static void RunOnUiThread(Action action)
    {
        MainThreadUtils.RunInMainThread(action).Wait();
    }
}
