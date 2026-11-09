// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT;

namespace SinePlayer.Core.Playback.Subtitle;

internal sealed partial class SubtitleSurface : IDisposable
{
    internal static IDirect3DDevice CreateWin2DDevice(D3D11Device device)
    {
        var dxgiDevice = (IDXGIDevice)device.Device;

        HRESULT created = PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IInspectable graphicsDevice);
        if (created.Value < 0)
        {
            throw Marshal.GetExceptionForHR(created.Value) ?? new InvalidOperationException($"Unable to wrap the device for Win2D (0x{created.Value:X8}).");
        }

        nint pointer = Marshal.GetIUnknownForObject(graphicsDevice);
        try
        {
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(pointer);
        }
        finally
        {
            // WinRT's marshaller took its own reference; this one was only for the hand-off.
            Marshal.Release(pointer);
        }
    }

    private bool _disposed;
    private D3D11Device? _d3dDevice;
    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _bitmap;
    private IVideoRenderTarget? _target;
    private SubtitleCompositor? _compositor;

    internal int Width => _bitmap is null ? 0 : (int)Math.Round(_bitmap.Size.Width);

    internal int Height => _bitmap is null ? 0 : (int)Math.Round(_bitmap.Size.Height);

    internal double CompositionScale => _compositor?.CompositionScale ?? 1.0;

    /// <summary>The layer's pixels, as the compositor draws them over the swap chain.</summary>
    internal CanvasRenderTarget? Bitmap => _bitmap;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _compositor?.RemoveLayer(this);
        _compositor?.Release();
        _compositor = null;
        _target = null;

        _bitmap?.Dispose();
        _bitmap = null;

        _canvasDevice?.Dispose();
        _canvasDevice = null;

        if (_d3dDevice is not null)
        {
            _d3dDevice = null;
            SharedSubtitleDevice.Return();
        }

        GC.SuppressFinalize(this);
    }

    internal void BindTarget(IVideoRenderTarget target)
    {
        if (ReferenceEquals(_target, target))
        {
            return;
        }

        _compositor?.RemoveLayer(this);
        _compositor?.Release();
        _compositor = null;

        _target = target;
        _compositor = SubtitleCompositor.Acquire(target);
        _compositor.AddLayer(this);
    }

    internal CanvasDevice EnsureCanvasDevice()
    {
        if (_compositor is { } compositor)
        {
            return compositor.EnsureCanvasDevice();
        }

        _canvasDevice ??= CanvasDevice.CreateFromDirect3D11Device(CreateWin2DDevice(EnsureDevice()));

        return _canvasDevice;
    }

    internal OperationResult EnsureSurface()
    {
        IVideoRenderTarget? target = _target;
        if (target is null || _compositor is null)
        {
            return OperationResult.Failure("The subtitle renderer has no surface to draw into.");
        }

        double scale = target.RasterizationScale > 0 && double.IsFinite(target.RasterizationScale) ? target.RasterizationScale : 1.0;
        int width = Math.Max(1, (int)Math.Round(target.SurfaceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(target.SurfaceHeight * scale));

        try
        {
            if (_bitmap is not null && ((int)Math.Round(_bitmap.Size.Width) != width || (int)Math.Round(_bitmap.Size.Height) != height))
            {
                _bitmap.Dispose();
                _bitmap = null;
            }

            _bitmap ??= new CanvasRenderTarget(EnsureCanvasDevice(), width, height, 96.0f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);

            // The panel is handed its swap chain when the renderer is configured, as it always was, rather than at the
            // first frame: the compositor owns that chain, but preparing it is this layer's to ask for.
            return _compositor.PrepareSwapChain();
        }
        catch (Exception exception)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            return OperationResult.Failure($"Unable to prepare the subtitle surface: {exception.Message}");
        }
    }

    internal OperationResult DrawAndPresent(Action<CanvasDrawingSession> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        SubtitleCompositor? compositor = _compositor;
        if (compositor is null)
        {
            return OperationResult.Failure("The subtitle surface has no target to present into.");
        }

        return compositor.Render(this, draw);
    }

    internal OperationResult DrawLayer(Action<CanvasDrawingSession> draw)
    {
        CanvasRenderTarget? bitmap = _bitmap;
        if (bitmap is null)
        {
            return OperationResult.Failure("The subtitle layer has no bitmap to draw into.");
        }

        try
        {
            using (CanvasDrawingSession session = bitmap.CreateDrawingSession())
            {
                session.Clear(default(Color));
                draw(session);
            }

            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to draw the subtitles: {exception.Message}");
        }
    }

    private D3D11Device EnsureDevice()
    {
        if (_d3dDevice is not { } device)
        {
            OperationResult<D3D11Device> rented = SharedSubtitleDevice.Rent();
            if (!rented.IsSuccessful)
            {
                throw new InvalidOperationException(rented.Message);
            }

            device = rented.Result;
            _d3dDevice = device;
        }

        return device;
    }
}
