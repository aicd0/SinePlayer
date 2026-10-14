// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// A composition swap chain the renderer draws into and presents. Composition is what lets the frames
/// reach a XAML element without the engine knowing anything about XAML: the host is handed the swap
/// chain handle and binds it to whatever element it owns.
/// </summary>
internal sealed class D3D11SwapChain : IDisposable
{
    private const uint BufferCount = 2;

    private readonly D3D11Device _device;

    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _view;
    private bool _disposed;

    private D3D11SwapChain(D3D11Device device, IDXGISwapChain1 swapChain, ID3D11Texture2D backBuffer, ID3D11RenderTargetView view, int width, int height)
    {
        _device = device;
        _swapChain = swapChain;
        _backBuffer = backBuffer;
        _view = view;
        Width = width;
        Height = height;
    }

    internal ID3D11RenderTargetView View => _view ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal static bool TryCreate(D3D11Device device, int width, int height, out D3D11SwapChain? swapChain, out string error)
    {
        swapChain = null;
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        try
        {
            Guid factoryId = typeof(IDXGIFactory2).GUID;
            HRESULT factoryResult = PInvoke.CreateDXGIFactory2(default, factoryId, out object factoryObject);
            if (factoryResult.Value < 0 || factoryObject is not IDXGIFactory2 factory)
            {
                error = $"Unable to create a DXGI factory: {factoryResult}";
                return false;
            }

            DXGI_SWAP_CHAIN_DESC1 description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            Graphics_Dxgi_IDXGIFactory2_Extensions.CreateSwapChainForComposition(factory, device.Device, description, null, out IDXGISwapChain1 created);
            if (created is null)
            {
                error = "Unable to create a composition swap chain.";
                return false;
            }

            ID3D11Texture2D backBuffer = GetBackBuffer(created);
            ID3D11RenderTargetView view = D3D11Interop.CreateRenderTargetView(device.Device, backBuffer);

            swapChain = new D3D11SwapChain(device, created, backBuffer, view, width, height);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Unable to create a composition swap chain: {exception.Message}";
            return false;
        }
    }

    /// <summary>Hands the swap chain to the host, which binds it to its own presentation element.</summary>
    internal PlaybackResult AttachTo(IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed || _swapChain is null)
        {
            return PlaybackResult.Failure("The swap chain has already been disposed.");
        }

        nint handle = Marshal.GetIUnknownForObject(_swapChain);
        try
        {
            target.AttachPresentationSurface(handle);
        }
        catch (Exception exception)
        {
            return PlaybackResult.Failure($"Unable to attach the presentation surface: {exception.Message}");
        }
        finally
        {
            // The target takes its own reference; ours was only for the hand-off.
            Marshal.Release(handle);
        }

        return PlaybackResult.Success;
    }

    /// <summary>Reads the current back buffer into system memory, for capture and verification.</summary>
    internal byte[] ReadPixels()
    {
        ID3D11Texture2D backBuffer = _backBuffer ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));
        return D3D11Interop.ReadPixels(_device.Device, _device.Context, backBuffer, Width, Height);
    }
    internal PlaybackResult Resize(int width, int height)
    {
        if (_disposed)
        {
            return PlaybackResult.Failure("The swap chain has already been disposed.");
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return PlaybackResult.Success;
        }

        try
        {
            // Buffers cannot be resized while they are still bound.
            _device.Context.OMSetRenderTargets(0, null, null);
            ReleaseBackBuffer();

            _swapChain!.ResizeBuffers(BufferCount, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, default);

            _backBuffer = GetBackBuffer(_swapChain);
            _view = D3D11Interop.CreateRenderTargetView(_device.Device, _backBuffer);
            Width = width;
            Height = height;
            return PlaybackResult.Success;
        }
        catch (Exception exception)
        {
            return PlaybackResult.Failure($"Unable to resize the swap chain: {exception.Message}");
        }
    }

    internal PlaybackResult Present()
    {
        if (_disposed || _swapChain is null)
        {
            return PlaybackResult.Failure("The swap chain has already been disposed.");
        }

        // Presenting on the vertical blank keeps playback paced to the display while leaving the
        // pipeline free to run ahead by the buffer count.
        HRESULT result = _swapChain.Present(1, default);
        return result.Value < 0
            ? PlaybackResult.Failure($"Unable to present a frame: {result}")
            : PlaybackResult.Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseBackBuffer();
        D3D11Interop.Release(_swapChain);
        _swapChain = null;
    }

    private static unsafe ID3D11Texture2D GetBackBuffer(IDXGISwapChain1 swapChain)
    {
        Guid textureId = typeof(ID3D11Texture2D).GUID;
        swapChain.GetBuffer(0, &textureId, out object surface);
        return (ID3D11Texture2D)surface;
    }

    private void ReleaseBackBuffer()
    {
        D3D11Interop.Release(_view);
        _view = null;
        D3D11Interop.Release(_backBuffer);
        _backBuffer = null;
    }
}