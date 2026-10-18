// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// A surface the renderer draws into. An offscreen target is what the tests and frame capture use; a
/// swap chain back buffer is the same thing with a different owner, which is why the renderer only
/// ever sees the view.
/// </summary>
internal sealed class D3D11RenderTarget : IDisposable
{
    private ID3D11Texture2D? _texture;
    private ID3D11RenderTargetView? _view;
    private bool _disposed;

    private D3D11RenderTarget(ID3D11Texture2D texture, ID3D11RenderTargetView view, int width, int height)
    {
        _texture = texture;
        _view = view;
        Width = width;
        Height = height;
    }

    internal ID3D11Texture2D Texture => _texture ?? throw new ObjectDisposedException(nameof(D3D11RenderTarget));

    internal ID3D11RenderTargetView View => _view ?? throw new ObjectDisposedException(nameof(D3D11RenderTarget));

    internal int Width { get; }

    internal int Height { get; }

    internal static D3D11RenderTarget CreateOffscreen(D3D11Device device, int width, int height)
    {
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };

        ID3D11Texture2D texture = D3D11Interop.CreateTexture2D(device.Device, description);
        ID3D11RenderTargetView view = D3D11Interop.CreateRenderTargetView(device.Device, texture);
        return new D3D11RenderTarget(texture, view, width, height);
    }

    /// <summary>Copies the surface into system memory as BGRA bytes, top row first.</summary>
    internal byte[] ReadPixels(D3D11Device device)
    {
        return D3D11Interop.ReadPixels(device.Device, device.Context, Texture, Width, Height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        D3D11Interop.Release(_view);
        _view = null;
        D3D11Interop.Release(_texture);
        _texture = null;
    }
}