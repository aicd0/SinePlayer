// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Graphics.DirectX.Direct3D11;
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

    private readonly IVideoRenderTarget _target;
    private readonly Lock _drawLock = new();
    private readonly List<SubtitleSurface> _layers = [];
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
            ReleaseSurface();

            if (_device is not null)
            {
                _device = null;
                SharedSubtitleDevice.Return();
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

    internal void AddLayer(SubtitleSurface layer)
    {
        lock (_drawLock)
        {
            if (!_layers.Contains(layer))
            {
                _layers.Add(layer);
            }
        }
    }

    internal void RemoveLayer(SubtitleSurface layer)
    {
        lock (_drawLock)
        {
            _layers.Remove(layer);
        }
    }

    internal OperationResult Render(SubtitleSurface layer, Action<CanvasDrawingSession> draw)
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

            OperationResult drawn = layer.DrawLayer(draw);
            if (!drawn.IsSuccessful)
            {
                return drawn;
            }

            return Compose();
        }
    }

    internal OperationResult PrepareSwapChain()
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
                foreach (SubtitleSurface layer in _layers)
                {
                    if (layer.Bitmap is { } bitmap)
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

    internal CanvasDevice EnsureCanvasDevice()
    {
        lock (_drawLock)
        {
            _canvasDevice ??= CanvasDevice.CreateFromDirect3D11Device(SubtitleSurface.CreateWin2DDevice(EnsureDevice()));
            return _canvasDevice;
        }
    }

    private D3D11Device EnsureDevice()
    {
        if (_device is not { } device)
        {
            OperationResult<D3D11Device> rented = SharedSubtitleDevice.Rent();
            if (!rented.IsSuccessful)
            {
                throw new InvalidOperationException(rented.Message);
            }

            device = rented.Result;
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
