// Copyright (c) aicd0. All rights reserved.
// Licensed under the MIT License.

using System;

using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

/// <summary>
/// Draws decoded frames with Direct3D 11. A frame is uploaded into one texture per plane and sampled
/// by a pixel shader that produces RGBA, so colour conversion, scaling and later post effects all
/// happen on the GPU rather than in a copy on the CPU.
/// <para>
/// Two output modes share the same draw path: a swap chain that presents to the host, and a plain
/// render target that frames are read back from, which is how the pipeline is verified without a
/// window.
/// </para>
/// </summary>
internal sealed class D3D11VideoRenderer : IVideoRenderer
{
    private readonly D3D11Device _device;
    private readonly D3D11VideoShaders _shaders;

    private ID3D11Texture2D[] _planeTextures = [];
    private ID3D11ShaderResourceView[] _planeViews = [];
    private int _frameWidth;
    private int _frameHeight;
    private PixelFormat _frameFormat = PixelFormat.Unknown;

    private ID3D11RenderTargetView? _outputView;
    private int _outputWidth;
    private int _outputHeight;

    private IVideoRenderTarget? _target;
    private D3D11SwapChain? _swapChain;
    private bool _needsResize;
    private bool _disposed;

    private D3D11VideoRenderer(D3D11Device device, D3D11VideoShaders shaders)
    {
        _device = device;
        _shaders = shaders;
    }

    public string Name => "d3d11";

    internal static bool TryCreate(D3D11Device device, out D3D11VideoRenderer? renderer, out string error)
    {
        renderer = null;

        if (!D3D11VideoShaders.TryCreate(device, out D3D11VideoShaders? shaders, out error))
        {
            return false;
        }

        renderer = new D3D11VideoRenderer(device, shaders!);
        error = string.Empty;
        return true;
    }

    /// <summary>Sets the surface frames are drawn into when there is no presentation target.</summary>
    internal void SetOutput(ID3D11RenderTargetView? view, int width, int height)
    {
        _outputView = view;
        _outputWidth = width;
        _outputHeight = height;
    }

    public PlaybackResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return PlaybackResult.Failure("The renderer has already been disposed.");
        }

        if (!ReferenceEquals(_target, target))
        {
            if (_target is not null)
            {
                _target.SurfaceSizeChanged -= OnSurfaceSizeChanged;
            }

            _target = target;
            target.SurfaceSizeChanged += OnSurfaceSizeChanged;
        }

        (int width, int height) = GetSurfaceSize(target);

        if (_swapChain is null)
        {
            if (!D3D11SwapChain.TryCreate(_device, width, height, out D3D11SwapChain? created, out string error))
            {
                return PlaybackResult.Failure(error);
            }

            PlaybackResult attach = created!.AttachTo(target);
            if (!attach.IsSuccessful)
            {
                created.Dispose();
                return attach;
            }

            _swapChain = created;
        }
        else
        {
            PlaybackResult resize = _swapChain.Resize(width, height);
            if (!resize.IsSuccessful)
            {
                return resize;
            }
        }

        _needsResize = false;
        SetOutput(_swapChain.View, _swapChain.Width, _swapChain.Height);
        return PlaybackResult.Success;
    }

    public PlaybackResult Render(RawVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            return PlaybackResult.Failure("The renderer has already been disposed.");
        }

        if (_swapChain is not null && _needsResize)
        {
            PlaybackResult resize = ApplyPendingResize();
            if (!resize.IsSuccessful)
            {
                return resize;
            }
        }

        if (_outputView is null || _outputWidth <= 0 || _outputHeight <= 0)
        {
            return PlaybackResult.Failure("The renderer has no output surface.");
        }

        if (frame.Buffer is not IPlanarVideoBuffer buffer)
        {
            return PlaybackResult.Failure("The frame does not expose readable planes.");
        }

        if (!_shaders.TryGetPixelShader(frame.Format, out ID3D11PixelShader? pixelShader))
        {
            return PlaybackResult.Failure($"The Direct3D renderer cannot display {frame.Format} frames.");
        }

        PlaybackResult upload = Upload(frame, buffer);
        if (!upload.IsSuccessful)
        {
            return upload;
        }

        ID3D11DeviceContext context = _device.Context;
        context.OMSetRenderTargets(1, [_outputView], null);

        // Anything the picture does not reach has to be painted, or what is left of the frame before it
        // shows through: the surface is a window, not the video.
        context.ClearRenderTargetView(_outputView, [0.0f, 0.0f, 0.0f, 1.0f]);

        // Drawn into the part of the surface the frame's shape calls for rather than the whole of it, so
        // a picture whose shape differs from the window's is letterboxed instead of stretched.
        (int fittedX, int fittedY, int fittedWidth, int fittedHeight) = VideoFit.Fit(frame.Width, frame.Height, _outputWidth, _outputHeight);
        D3D11Interop.SetViewport(context, fittedX, fittedY, fittedWidth, fittedHeight);
        context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11Interop.SetShaders(context, _shaders.VertexShader, pixelShader!);
        D3D11Interop.SetTextures(context, _planeViews, _shaders.Sampler);

        // A single triangle covers the whole target, so no vertex or index buffer is needed.
        context.Draw(3, 0);

        return _swapChain is null ? PlaybackResult.Success : _swapChain.Present();
    }

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
            _target = null;
        }

        _swapChain?.Dispose();
        _swapChain = null;

        ReleaseFrameTextures();
        _shaders.Dispose();
    }

    /// <summary>
    /// Texture per plane of a frame layout. Chroma planes of a 4:2:0 layout are half the size in both
    /// directions.
    /// </summary>
    private static (DXGI_FORMAT Format, int Width, int Height)[] GetPlaneLayout(PixelFormat format, int width, int height)
    {
        int chromaWidth = Math.Max(1, (width + 1) / 2);
        int chromaHeight = Math.Max(1, (height + 1) / 2);

        return format switch
        {
            PixelFormat.Bgra8 or PixelFormat.Rgba8 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, width, height),
            ],
            PixelFormat.Nv12 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.Yuv420P =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, chromaHeight),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, chromaHeight),
            ],
            _ => [],
        };
    }

    private static (int Width, int Height) GetSurfaceSize(IVideoRenderTarget target)
    {
        double scale = target.RasterizationScale > 0 ? target.RasterizationScale : 1.0;
        return (Math.Max(1, (int)Math.Round(target.SurfaceWidth * scale)), Math.Max(1, (int)Math.Round(target.SurfaceHeight * scale)));
    }

    private void OnSurfaceSizeChanged()
    {
        _needsResize = true;
    }

    private PlaybackResult ApplyPendingResize()
    {
        if (_swapChain is null || _target is null)
        {
            return PlaybackResult.Success;
        }

        (int width, int height) = GetSurfaceSize(_target);
        PlaybackResult resize = _swapChain.Resize(width, height);
        if (!resize.IsSuccessful)
        {
            return resize;
        }

        _needsResize = false;
        SetOutput(_swapChain.View, _swapChain.Width, _swapChain.Height);
        return PlaybackResult.Success;
    }

    private PlaybackResult Upload(RawVideoFrame frame, IPlanarVideoBuffer buffer)
    {
        (DXGI_FORMAT Format, int Width, int Height)[] layout = GetPlaneLayout(frame.Format, frame.Width, frame.Height);
        if (layout.Length == 0)
        {
            return PlaybackResult.Failure($"The Direct3D renderer cannot upload {frame.Format} frames.");
        }

        if (buffer.PlaneCount < layout.Length)
        {
            return PlaybackResult.Failure($"The frame has {buffer.PlaneCount} planes but {frame.Format} needs {layout.Length}.");
        }

        if (_planeTextures.Length != layout.Length || _frameWidth != frame.Width || _frameHeight != frame.Height || _frameFormat != frame.Format)
        {
            ReleaseFrameTextures();
            CreateFrameTextures(layout);
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _frameFormat = frame.Format;
        }

        for (int plane = 0; plane < layout.Length; plane++)
        {
            nint pixels = buffer.GetPlanePointer(plane);
            int stride = buffer.GetPlaneStride(plane);
            if (pixels == 0 || stride <= 0)
            {
                return PlaybackResult.Failure($"Plane {plane} of the frame is not readable.");
            }

            // The box is expressed in texels, so the source row pitch stays whatever the decoder used.
            D3D11Interop.UploadTexture(_device.Context, _planeTextures[plane], layout[plane].Width, layout[plane].Height, pixels, stride);
        }

        return PlaybackResult.Success;
    }

    private void CreateFrameTextures((DXGI_FORMAT Format, int Width, int Height)[] layout)
    {
        _planeTextures = new ID3D11Texture2D[layout.Length];
        _planeViews = new ID3D11ShaderResourceView[layout.Length];

        for (int plane = 0; plane < layout.Length; plane++)
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = (uint)layout[plane].Width,
                Height = (uint)layout[plane].Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = layout[plane].Format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };

            _planeTextures[plane] = D3D11Interop.CreateTexture2D(_device.Device, description);
            _planeViews[plane] = D3D11Interop.CreateShaderResourceView(_device.Device, _planeTextures[plane]);
        }
    }

    private void ReleaseFrameTextures()
    {
        foreach (ID3D11ShaderResourceView view in _planeViews)
        {
            D3D11Interop.Release(view);
        }

        foreach (ID3D11Texture2D texture in _planeTextures)
        {
            D3D11Interop.Release(texture);
        }

        _planeViews = [];
        _planeTextures = [];
        _frameFormat = PixelFormat.Unknown;
        _frameWidth = 0;
        _frameHeight = 0;
    }
}