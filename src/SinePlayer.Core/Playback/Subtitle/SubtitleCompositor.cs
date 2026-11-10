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
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.WinRT;

namespace SinePlayer.Core.Playback.Subtitle;

internal sealed partial class SubtitleCompositor : IDisposable
{
    private static readonly Lock _targetsLock = new();
    private static readonly Dictionary<IVideoRenderTarget, SubtitleCompositor> _targets = [];

    internal static SubtitleCompositor Acquire(IVideoRenderTarget target)
    {
        lock (_targetsLock)
        {
            if (!_targets.TryGetValue(target, out SubtitleCompositor? compositor))
            {
                compositor = new SubtitleCompositor(target);
                _targets.Add(target, compositor);
            }

            compositor._shares++;
            return compositor;
        }
    }

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

    private readonly IVideoRenderTarget _target;
    private readonly Lock _drawLock = new();
    private readonly List<object> _layers = [];
    private readonly Dictionary<object, CanvasRenderTarget> _bitmaps = [];
    private CanvasDevice? _canvasDevice;

    private int _shares;
    private bool _disposed;
    private D3D11Device? _device;
    private D3D11SwapChain? _swapChain;
    private IDirect3DSurface? _surface;

    private SubtitleCompositor(IVideoRenderTarget target)
    {
        _target = target;
    }

    internal int Width => _swapChain?.Width ?? 0;

    internal int Height => _swapChain?.Height ?? 0;

    internal double CompositionScale => _swapChain?.CompositionScale ?? 1.0;

    public void Dispose()
    {
        lock (_drawLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _layers.Clear();

            foreach (CanvasRenderTarget bitmap in _bitmaps.Values)
            {
                bitmap.Dispose();
            }

            _bitmaps.Clear();

            ReleaseSurface();

            if (_device is { } device)
            {
                _device = null;
                device.Dispose();
            }
        }
    }

    internal void Release()
    {
        lock (_targetsLock)
        {
            _shares--;
            if (_shares > 0)
            {
                return;
            }

            _targets.Remove(_target);
        }

        Dispose();
    }

    internal CanvasDevice EnsureCanvasDevice()
    {
        lock (_drawLock)
        {
            _canvasDevice ??= CanvasDevice.CreateFromDirect3D11Device(CreateWin2DDevice(EnsureDevice()));
            return _canvasDevice;
        }
    }

    internal void AddLayer(object key)
    {
        lock (_drawLock)
        {
            if (!_layers.Contains(key))
            {
                _layers.Add(key);
            }
        }
    }

    internal void RemoveLayer(object key)
    {
        lock (_drawLock)
        {
            _layers.Remove(key);

            // The bitmap is the compositor's, so leaving the composition is what retires it — under the lock that excludes a
            // frame drawing into it.
            if (_bitmaps.Remove(key, out CanvasRenderTarget? bitmap))
            {
                bitmap.Dispose();
            }
        }
    }

    internal OperationResult Prepare(object key, out int width, out int height)
    {
        lock (_drawLock)
        {
            width = 0;
            height = 0;
            if (_disposed)
            {
                return OperationResult.Failure("The subtitle compositor has been disposed.");
            }

            OperationResult prepared = PrepareSwapChain();
            if (!prepared.IsSuccessful)
            {
                return prepared;
            }

            return EnsureBitmap(key, out width, out height);
        }
    }

    internal OperationResult Render(object key, Action<CanvasDrawingSession, int, int> draw)
    {
        lock (_drawLock)
        {
            if (_disposed)
            {
                return OperationResult.Failure("The subtitle compositor has been disposed.");
            }

            OperationResult prepared = PrepareSwapChain();
            if (!prepared.IsSuccessful)
            {
                return prepared;
            }

            // The size is prepared here rather than by the caller, so the words are laid out for the buffer they are drawn
            // into even when a resize arrives between the two, and the very size is handed to the drawing.
            OperationResult sized = EnsureBitmap(key, out int width, out int height);
            if (!sized.IsSuccessful)
            {
                return sized;
            }

            OperationResult drawn = DrawLayer(key, width, height, draw);
            if (!drawn.IsSuccessful)
            {
                return drawn;
            }

            return Compose();
        }
    }

    private OperationResult EnsureBitmap(object key, out int width, out int height)
    {
        double scale = _target.RasterizationScale > 0 && double.IsFinite(_target.RasterizationScale) ? _target.RasterizationScale : 1.0;
        int wantedWidth = Math.Max(1, (int)Math.Round(_target.SurfaceWidth * scale));
        int wantedHeight = Math.Max(1, (int)Math.Round(_target.SurfaceHeight * scale));

        width = 0;
        height = 0;
        try
        {
            if (_bitmaps.TryGetValue(key, out CanvasRenderTarget? existing)
                && ((int)Math.Round(existing.Size.Width) != wantedWidth || (int)Math.Round(existing.Size.Height) != wantedHeight))
            {
                _bitmaps.Remove(key);
                existing.Dispose();
                existing = null;
            }

            if (existing is null)
            {
                existing = new CanvasRenderTarget(EnsureCanvasDevice(), wantedWidth, wantedHeight, 96.0f, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
                _bitmaps[key] = existing;
            }

            width = (int)Math.Round(existing.Size.Width);
            height = (int)Math.Round(existing.Size.Height);
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            if (_bitmaps.Remove(key, out CanvasRenderTarget? broken))
            {
                broken.Dispose();
            }

            return OperationResult.Failure($"Unable to prepare the subtitle surface: {exception.Message}");
        }
    }

    private OperationResult DrawLayer(object key, int width, int height, Action<CanvasDrawingSession, int, int> draw)
    {
        if (!_bitmaps.TryGetValue(key, out CanvasRenderTarget? bitmap))
        {
            return OperationResult.Failure("The subtitle layer has no bitmap to draw into.");
        }

        try
        {
            using (CanvasDrawingSession session = bitmap.CreateDrawingSession())
            {
                session.Clear(default(Color));
                draw(session, width, height);
            }

            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to draw the subtitles: {exception.Message}");
        }
    }

    private OperationResult PrepareSwapChain()
    {
        double scale = _target.RasterizationScale > 0 && double.IsFinite(_target.RasterizationScale) ? _target.RasterizationScale : 1.0;
        int width = Math.Max(1, (int)Math.Round(_target.SurfaceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(_target.SurfaceHeight * scale));

        try
        {
            if (_swapChain is not null && (_swapChain.Width != width || _swapChain.Height != height || _swapChain.CompositionScale != scale))
            {
                // A chain drawn through Win2D is replaced rather than resized: the wrapper this compositor holds around its
                // back buffer is a WinRT object whose reference DXGI counts, and ResizeBuffers refuses while any reference
                // to a buffer is outstanding. A new chain has new buffers and nothing holding them.
                ReleaseSurface();
            }

            if (_swapChain is null)
            {
                OperationResult<D3D11SwapChain> created = D3D11SwapChain.Create(EnsureDevice(), width, height, scale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED);
                if (!created.IsSuccessful)
                {
                    return OperationResult.Failure(created);
                }

                OperationResult attached = created.Result.AttachTo(_target);
                if (!attached.IsSuccessful)
                {
                    created.Result.Dispose();
                    return attached;
                }

                _swapChain = created.Result;
                _surface = CreateSurface(created.Result);
            }

            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            ReleaseSurface();
            return OperationResult.Failure($"Unable to prepare the subtitle surface: {exception.Message}");
        }
    }

    private OperationResult Compose()
    {
        D3D11SwapChain? swapChain = _swapChain;
        IDirect3DSurface? surface = _surface;
        if (swapChain is null || surface is null)
        {
            return OperationResult.Failure("The subtitle surface has no back buffer to draw into.");
        }

        CanvasRenderTarget? target = null;
        try
        {
            target = CanvasRenderTarget.CreateFromDirect3D11Surface(EnsureCanvasDevice(), surface, 96.0f, CanvasAlphaMode.Premultiplied);

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Windows.UI.Color));
                foreach (object key in _layers)
                {
                    if (_bitmaps.TryGetValue(key, out CanvasRenderTarget? bitmap))
                    {
                        session.DrawImage(bitmap);
                    }
                }
            }

            return swapChain.Present();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to compose the subtitles: {exception.Message}");
        }
        finally
        {
            target?.Dispose();
        }
    }

    private D3D11Device EnsureDevice()
    {
        if (_device is not { } device)
        {
            OperationResult<D3D11Device> created = D3D11Device.Create();
            if (!created.IsSuccessful)
            {
                throw new InvalidOperationException(created.Message);
            }

            device = created.Result;
            _device = device;
        }

        return device;
    }

    private void ReleaseSurface()
    {
        _surface = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }
}
