// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.WinRT;

namespace SinePlayer.Core.Playback.Backends.Win2D;

internal sealed partial class SubtitleSurface : IDisposable
{
    /// <summary>
    /// The Win2D device over the engine's Direct3D device, so drawing reaches the same swap chain rather than a device
    /// of Win2D's own choosing. A Direct3D device is also a DXGI device, which is what the exporter wants; the WinRT object
    /// it produces is another projection of the same thing, so it is handed to WinRT's marshaller to become the type Win2D
    /// takes.
    /// </summary>
    private static IDirect3DDevice CreateWin2DDevice(D3D11Device device)
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

    /// <summary>The swap chain's back buffer as the surface WinRT wraps, then as the surface type Win2D takes.</summary>
    private static IDirect3DSurface CreateSurface(D3D11SwapChain swapChain)
    {
        var dxgiSurface = (IDXGISurface)swapChain.BackBuffer;

        HRESULT created = PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out IInspectable graphicsSurface);
        if (created.Value < 0)
        {
            throw Marshal.GetExceptionForHR(created.Value) ?? new InvalidOperationException($"Unable to wrap the surface for Win2D (0x{created.Value:X8}).");
        }

        nint pointer = Marshal.GetIUnknownForObject(graphicsSurface);
        try
        {
            return WinRT.MarshalInspectable<IDirect3DSurface>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private bool _disposed;
    private D3D11Device? _d3dDevice;
    private D3D11SwapChain? _swapChain;
    private IDirect3DSurface? _surface;
    private CanvasDevice? _canvasDevice;
    private IVideoRenderTarget? _target;

    internal int Width => _swapChain?.Width ?? 0;

    internal int Height => _swapChain?.Height ?? 0;

    internal double CompositionScale => _swapChain?.CompositionScale ?? 1.0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_target is not null)
        {
            _target.SurfaceSizeChanged -= OnSurfaceSizeChanged;
        }

        ResetSurface();
        _target = null;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Takes the host's element, letting go of the previous one. It is the surface that follows a target, not the other
    /// way round: a renderer is configured once per track and a size change is answered when it next draws.
    /// </summary>
    internal void BindTarget(IVideoRenderTarget target)
    {
        if (!ReferenceEquals(_target, target) && _target is not null)
        {
            _target.SurfaceSizeChanged -= OnSurfaceSizeChanged;
            _target = null;
        }

        _target = target;
    }

    /// <summary>
    /// The device text and pictures are drawn with, built on first use. A test that wants to check a drawing against an
    /// offscreen target uses this rather than a swap chain, which cannot be read back after it has presented.
    /// </summary>
    internal CanvasDevice EnsureCanvasDevice()
    {
        if (_canvasDevice is null)
        {
            if (_d3dDevice is null)
            {
                OperationResult<D3D11Device> created = D3D11Device.Create();
                if (!created.IsSuccessful)
                {
                    throw new InvalidOperationException(created.Message);
                }

                _d3dDevice = created.Result;
            }

            _canvasDevice = CanvasDevice.CreateFromDirect3D11Device(CreateWin2DDevice(_d3dDevice!));
        }

        return _canvasDevice;
    }

    /// <summary>
    /// Prepares a surface of the size the host now asks for, replacing one of another size or scale. Cheap when nothing
    /// changed, which is why every drawing calls it rather than trusting what it was told in <c>Configure</c>.
    /// </summary>
    internal OperationResult EnsureSurface()
    {
        IVideoRenderTarget? target = _target;
        if (target is null)
        {
            return OperationResult.Failure("The subtitle renderer has no surface to draw into.");
        }

        double scale = target.RasterizationScale > 0 && double.IsFinite(target.RasterizationScale) ? target.RasterizationScale : 1.0;
        int width = Math.Max(1, (int)Math.Round(target.SurfaceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(target.SurfaceHeight * scale));

        try
        {
            if (_d3dDevice is null || _canvasDevice is null)
            {
                EnsureCanvasDevice();
            }

            if (_swapChain is not null && (ScaleChanged(_swapChain, width, height, scale)))
            {
                // A chain drawn through Win2D is replaced rather than resized. The wrapper this renderer holds around the
                // chain's back buffer is a WinRT object whose reference DXGI counts, and ResizeBuffers refuses while any
                // reference to a buffer is outstanding — the wrapper lives until its finalizer runs, so a resize fails
                // with DXGI_ERROR_INVALID_CALL and would keep failing. A new chain has new buffers and nothing holding
                // them, which is why the size change is answered by throwing the old one away.
                ReleaseChain();
            }

            if (_swapChain is null)
            {
                OperationResult<D3D11SwapChain> created = D3D11SwapChain.Create(_d3dDevice!, width, height, scale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED);
                if (!created.IsSuccessful)
                {
                    return OperationResult.Failure(created);
                }

                OperationResult attached = created.Result.AttachTo(target);
                if (!attached.IsSuccessful)
                {
                    created.Result.Dispose();
                    return attached;
                }

                _swapChain = created.Result;
                _surface = CreateSurface(created.Result);
                target.SurfaceSizeChanged += OnSurfaceSizeChanged;
            }

            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            ResetSurface();
            return OperationResult.Failure($"Unable to prepare the subtitle surface: {exception.Message}");
        }
    }

    /// <summary>
    /// Clears the surface, hands it to <paramref name="draw"/>, and presents it. The clearing is part of the step because
    /// a subtitle surface is transparent where nothing is drawn: what is left of the last frame would otherwise stay.
    /// </summary>
    internal OperationResult DrawAndPresent(Action<CanvasDrawingSession> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        D3D11SwapChain? swapChain = _swapChain;
        IDirect3DSurface? surface = _surface;
        CanvasDevice? device = _canvasDevice;
        if (swapChain is null || surface is null || device is null)
        {
            return OperationResult.Failure("The subtitle surface has no back buffer to draw into.");
        }

        CanvasRenderTarget? target = null;
        try
        {
            target = CanvasRenderTarget.CreateFromDirect3D11Surface(device, surface, 96.0f, CanvasAlphaMode.Premultiplied);

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Color));
                draw(session);
            }

            swapChain.Present();
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to draw the subtitles: {exception.Message}");
        }
        finally
        {
            target?.Dispose();
        }
    }

    private static bool ScaleChanged(D3D11SwapChain swapChain, int width, int height, double scale)
    {
        return swapChain.Width != width || swapChain.Height != height || swapChain.CompositionScale != scale;
    }

    /// <summary>
    /// Lets go of the chain and of the wrapper around its back buffer. Something that drew this chain cannot be resized
    /// afterwards, so a resize is answered by this: the chain goes, and the next surface is a new one.
    /// </summary>
    private void ReleaseChain()
    {
        _surface = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }

    private void OnSurfaceSizeChanged()
    {
        // A swap chain cannot be resized from the thread that raised this, and it does not need to be: the pipeline
        // draws again after telling the renderer, and EnsureSurface compares the size it is asked for with its own.
    }

    private void ResetSurface()
    {
        ReleaseChain();
        _canvasDevice?.Dispose();
        _canvasDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }
}
