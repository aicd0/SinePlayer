// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;
using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11.Surfaces;

using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Direct3D11;

internal sealed partial class D3D11VideoRenderer : IVideoRenderer, ID3D11GpuDevice
{
    #region Constructor

    /// <summary>The texture registers the array shader samples; the plane shaders use the three before them.</summary>
    private const uint GpuTextureSlot = 3;

    /// <summary>
    /// The layouts a hardware frame can be drawn from: one row per video format DXGI defines plane views for, with the
    /// two channel layouts those views are cast to. It is the only statement of that fact — the pre-check reads a row by
    /// layout, before any texture exists, and the draw reads the same row by the texture's own format. Two copies of it
    /// could disagree, and a pre-check wider than the draw would accept a frame that is then refused every time it
    /// arrives, which reads as a decoder fault rather than as this one.
    /// </summary>
    private static readonly (PixelFormat Layout, DXGI_FORMAT Texture, DXGI_FORMAT Luma, DXGI_FORMAT Chroma)[] sGpuLayouts =
    [
        (PixelFormat.Nv12, DXGI_FORMAT.DXGI_FORMAT_NV12, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM),
        (PixelFormat.P010, DXGI_FORMAT.DXGI_FORMAT_P010, DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
        (PixelFormat.P016, DXGI_FORMAT.DXGI_FORMAT_P016, DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
    ];

    internal static OperationResult<D3D11VideoRenderer> Create(D3D11Device device, VideoSurfacePreference surfacePreference)
    {
        OperationResultCreator<D3D11VideoRenderer> result = new();

        OperationResult<D3D11VideoShaders> shaders = D3D11VideoShaders.Create(device);
        if (!shaders.IsSuccessful)
        {
            return result.Failure(shaders);
        }

        return result.Success(new D3D11VideoRenderer(device, shaders.Result, surfacePreference));
    }

    private bool _disposed;
    private string _name;

    private readonly object _deviceLock = new();
    private readonly D3D11Device _device;
    private readonly D3D11VideoShaders _shaders;
    private readonly ID3D11Buffer _frameTransform;

    private ID3D11RenderTargetView? _outputView;
    private int _outputWidth;
    private int _outputHeight;

    private PixelFormat _frameFormat = PixelFormat.Unknown;
    private int _frameWidth;
    private int _frameHeight;

    private ID3D11PixelShader? _lastPixelShader;
    private ID3D11ShaderResourceView[] _lastViews = [];
    private uint _lastSlot;
    private (float X, float Y) _lastTexCoords = (1.0f, 1.0f);
    private (float X, float Y) _lastTexCoordMax = (1.0f, 1.0f);
    private (float X, float Y) _lastChromaTexCoordMax = (1.0f, 1.0f);

    private D3D11VideoRenderer(D3D11Device device, D3D11VideoShaders shaders, VideoSurfacePreference surfacePreference)
    {
        _device = device;
        _shaders = shaders;
        _surfacePreference = surfacePreference;
        _frameTransform = D3D11Interop.CreateConstantBuffer(device.Device);
        _name = Describe(kind: null);
    }

    #endregion

    #region Public API

    public string Name => _name;

    public IGpuDevice? GpuDevice => this;

    public nint NativeDevice => _device.NativeDevice;

    public object DeviceLock => _deviceLock;

    internal int OutputPixelWidth => _outputWidth;

    internal int OutputPixelHeight => _outputHeight;

    internal VideoSurfaceKind SurfaceKind
    {
        get => _surfaceKind;
        set => _surfaceKind = value;
    }

    public void Dispose()
    {
        lock (_deviceLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _target = null;

            _surface?.Dispose();
            _surface = null;

            ReleaseFrameTextures();
            D3D11Interop.Release(_frameTransform);
            _shaders.Dispose();

            _device.Dispose();
        }
    }

    public OperationResult Configure(VideoStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        lock (_deviceLock)
        {
            if (_disposed)
            {
                return OperationResult.Failure("The renderer has already been disposed.");
            }

            _target = target;

            (int width, int height, double scale) = GetSurfaceSize(target);

            if (_surface is null)
            {
                OperationResult<IVideoSurface> created = VideoSurfaceFactory.Create(_device, width, height, scale, _surfacePreference, stream.Colour);
                if (!created.IsSuccessful)
                {
                    return OperationResult.Failure(created);
                }

                OperationResult attach = created.Result.AttachTo(target);
                if (!attach.IsSuccessful)
                {
                    created.Result.Dispose();
                    return attach;
                }

                _surface = created.Result;
                _surfaceKind = created.Result.Kind;
                _name = Describe(_surfaceKind);
                _appliedSize = (width, height, scale);
                SetOutput(_surface.View, _surface.Width, _surface.Height);
            }

            return MatchSurfaceSize();
        }
    }

    public bool TryAcquireFrameSlot()
    {
        IVideoSurface? surface;
        lock (_deviceLock)
        {
            surface = _surface;
        }

        return surface is null || surface.TryAcquireFrameSlot();
    }

    public OperationResult Render(RawVideoFrame frame, VideoTransform transform)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            return OperationResult.Failure("The renderer has already been disposed.");
        }

        lock (_deviceLock)
        {
            OperationResult sized = MatchSurfaceSize();
            if (!sized.IsSuccessful)
            {
                return sized;
            }

            if (_outputView is null || _outputWidth <= 0 || _outputHeight <= 0)
            {
                return OperationResult.Failure("The renderer has no output surface.");
            }

            VideoSurfaceTransform surfaceTransform = VideoSurfaceTransforms.Select(_surfaceKind, frame.Colour);

            if (frame.Buffer is IGpuVideoBuffer gpuBuffer)
            {
                OperationResult bound = BindGpuFrame(frame, gpuBuffer);
                if (!bound.IsSuccessful)
                {
                    return bound;
                }

                OperationResult<ID3D11PixelShader> resolvedShader = _shaders.GetArrayShader(surfaceTransform);
                if (!resolvedShader.IsSuccessful)
                {
                    return OperationResult.Failure(resolvedShader);
                }

                return Draw(resolvedShader.Result, _gpuViews, GpuTextureSlot, _gpuTexCoords, _gpuTexCoordMax, _gpuChromaTexCoordMax, transform);
            }
            else
            {
                if (frame.Buffer is not IPlanarVideoBuffer buffer)
                {
                    return OperationResult.Failure("The frame does not expose readable planes.");
                }

                OperationResult<ID3D11PixelShader> resolvedShader = _shaders.GetPixelShader(frame.Format, surfaceTransform);
                if (!resolvedShader.IsSuccessful)
                {
                    return OperationResult.Failure(resolvedShader);
                }

                OperationResult upload = Upload(frame, buffer);
                if (!upload.IsSuccessful)
                {
                    return upload;
                }

                return Draw(resolvedShader.Result, _planeViews, 0, (1.0f, 1.0f), (1.0f, 1.0f), (1.0f, 1.0f), transform);
            }
        }
    }

    public OperationResult RefreshSurface(VideoTransform transform)
    {
        if (_disposed)
        {
            return OperationResult.Failure("The renderer has already been disposed.");
        }

        lock (_deviceLock)
        {
            OperationResult sized = MatchSurfaceSize();
            if (!sized.IsSuccessful)
            {
                return sized;
            }

            if (_lastPixelShader is null || _lastViews.Length == 0 || _outputView is null)
            {
                return OperationResult.Success();
            }

            return Draw(_lastPixelShader, _lastViews, _lastSlot, _lastTexCoords, _lastTexCoordMax, _lastChromaTexCoordMax, transform);
        }
    }

    public OperationResult ClearSurface()
    {
        if (_disposed)
        {
            return OperationResult.Failure("The renderer has already been disposed.");
        }

        lock (_deviceLock)
        {
            OperationResult sized = MatchSurfaceSize();
            if (!sized.IsSuccessful)
            {
                return sized;
            }

            _lastPixelShader = null;
            _lastViews = [];
            _lastSlot = 0;

            if (_outputView is null)
            {
                return OperationResult.Success();
            }

            _device.Context.OMSetRenderTargets(1, [_outputView], null);
            _device.Context.ClearRenderTargetView(_outputView, [0.0f, 0.0f, 0.0f, 1.0f]);

            return _surface is null ? OperationResult.Success() : _surface.Present();
        }
    }

    public OperationResult CanDisplayGpuFrame(PixelFormat format)
    {
        foreach ((PixelFormat layout, _, _, _) in sGpuLayouts)
        {
            if (layout == format)
            {
                return OperationResult.Success();
            }
        }

        return OperationResult.Failure($"The Direct3D renderer cannot sample {format} frames that are still on the GPU.");
    }

    /// <summary>Sets the surface frames are drawn into when there is no presentation target.</summary>
    internal void SetOutput(ID3D11RenderTargetView? view, int width, int height)
    {
        _outputView = view;
        _outputWidth = width;
        _outputHeight = height;
    }

    /// <summary>
    /// The scale transform the swap chain applies when the host composes it, which a test checks is the
    /// inverse of the display's scale — the pair to <see cref="OutputPixelWidth"/>.
    /// </summary>
    internal bool TryGetCompositionTransform(out double scaleX, out double scaleY)
    {
        scaleX = 1.0;
        scaleY = 1.0;

        return _surface is not null && _surface.TryGetCompositionTransform(out scaleX, out scaleY);
    }

    #endregion

    #region Hardware buffer

    private ID3D11ShaderResourceView[] _gpuViews = [];
    private (float X, float Y) _gpuTexCoords = (1.0f, 1.0f);
    private (float X, float Y) _gpuTexCoordMax = (1.0f, 1.0f);
    private (float X, float Y) _gpuChromaTexCoordMax = (1.0f, 1.0f);
    private IGpuVideoBuffer? _retainedFrame;
    private nint _gpuTexture;
    private int _gpuSlice = -1;

    private OperationResult BindGpuFrame(RawVideoFrame frame, IGpuVideoBuffer buffer)
    {
        // The views outlive the call, so the same slice of the same texture at the same picture size is already bound.
        // The texture's format belongs to the texture, which is why it need not be part of the comparison.
        if (_gpuViews.Length == 2 && _gpuTexture == buffer.NativeTexture && _gpuSlice == buffer.SubresourceIndex && _frameWidth == frame.Width && _frameHeight == frame.Height)
        {
            return OperationResult.Success();
        }

        var resource = (ID3D11Resource)Marshal.GetObjectForIUnknown(buffer.NativeTexture);
        try
        {
            if (resource is not ID3D11Texture2D texture)
            {
                return OperationResult.Failure("The decoded frame is not a two-dimensional texture.");
            }

            // What the texture holds, not what the frame says it holds: the layout was chosen by the decoder and the
            // driver before this renderer saw the frame, and a view may only be cast to a channel layout its format
            // allows. One channel for luma, two for interleaved chroma; ten-bit frames hold the same planes sixteen
            // bits wide, and a P010 or P016 texture refuses an eight-bit view, so the width follows the texture.
            D3D11_TEXTURE2D_DESC description = D3D11Interop.GetDescription(texture);

            DXGI_FORMAT lumaFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
            DXGI_FORMAT chromaFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
            foreach ((_, DXGI_FORMAT textureFormat, DXGI_FORMAT lumaView, DXGI_FORMAT chromaView) in sGpuLayouts)
            {
                if (textureFormat == description.Format)
                {
                    lumaFormat = lumaView;
                    chromaFormat = chromaView;
                    break;
                }
            }

            if (lumaFormat == DXGI_FORMAT.DXGI_FORMAT_UNKNOWN)
            {
                return OperationResult.Failure($"The Direct3D renderer cannot sample a {description.Format} texture.");
            }

            ID3D11ShaderResourceView? luma = CreateSliceView(texture, buffer.SubresourceIndex, lumaFormat);
            ID3D11ShaderResourceView? chroma = luma is null ? null : CreateSliceView(texture, buffer.SubresourceIndex, chromaFormat);

            if (luma is null || chroma is null)
            {
                D3D11Interop.Release(luma);
                D3D11Interop.Release(chroma);
                return OperationResult.Failure($"The device offers no shader view of the {description.Format} texture.");
            }

            // The views name the slice but do not pin it: the texture is the decoder's own, and it will hand this
            // slice out again as soon as the frame is gone. A frame that cannot be kept alive is therefore refused
            // before anything is adopted, rather than drawn from memory the decoder may already be refilling — a
            // torn picture is worse than a missing one, because nothing about it says what went wrong.
            IGpuVideoBuffer? retained = buffer.Retain();
            if (retained is null)
            {
                D3D11Interop.Release(luma);
                D3D11Interop.Release(chroma);
                return OperationResult.Failure("The decoded frame could not be kept alive for drawing, so its texture cannot be sampled.");
            }

            ReleaseFrameTextures();

            _gpuViews = [luma, chroma];
            _gpuTexture = buffer.NativeTexture;
            _gpuSlice = buffer.SubresourceIndex;
            _retainedFrame = retained;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _gpuTexCoords = GetTexCoordScale((int)description.Width, (int)description.Height, frame.Width, frame.Height);
            _gpuTexCoordMax = GetTexCoordMax((int)description.Width, (int)description.Height, frame.Width, frame.Height);
            _gpuChromaTexCoordMax = GetChromaTexCoordMax((int)description.Width, (int)description.Height, frame.Width, frame.Height);
            return OperationResult.Success();
        }
        finally
        {
            D3D11Interop.Release(resource);
        }
    }

    /// <summary>
    /// One plane of a semi-planar texture, as a view a shader can sample. Libavcodec stores a frame as an
    /// array of textures, so the view names the slice; the plane is selected by the view's format.
    /// </summary>
    private ID3D11ShaderResourceView? CreateSliceView(ID3D11Resource texture, int slice, DXGI_FORMAT format)
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC description = new()
        {
            Format = format,
            ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2DARRAY,
        };
        description.Anonymous.Texture2DArray = new D3D11_TEX2D_ARRAY_SRV
        {
            MostDetailedMip = 0,
            MipLevels = 1,
            FirstArraySlice = (uint)slice,
            ArraySize = 1,
        };

        return D3D11Interop.TryCreateShaderResourceView(_device.Device, texture, description);
    }

    /// <summary>
    /// How the picture is read out of its texture. A hardware decoder decodes into a texture it aligned for its own use, so
    /// a 1080-high picture can live in 1088 or 1152 rows: the rows below the picture were never written, and reading them
    /// would put a band of whatever they happen to contain — nothing, which converts to green — along the bottom.
    /// <para>
    /// The span is exact for the rows it covers: output pixel <c>i</c> of a picture <c>h</c> high reads
    /// <c>(i + 0.5) / texture</c>, the centre of texel <c>i</c>, so nothing is resampled, and any inset at the vertex stage
    /// would move every row to buy one. It does reach past the picture's own samples when the picture is drawn larger than
    /// itself, which is what <see cref="GetTexCoordMax"/> stops at the sample.
    /// </para>
    /// </summary>
    internal static (float X, float Y) GetTexCoordScale(int textureWidth, int textureHeight, int frameWidth, int frameHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0)
        {
            return (1.0f, 1.0f);
        }

        return (
            Math.Min(1.0f, frameWidth / (float)textureWidth),
            Math.Min(1.0f, frameHeight / (float)textureHeight));
    }

    /// <summary>
    /// The coordinate at which the picture's own samples end in its luma plane. A picture drawn larger than itself maps
    /// its rows onto more rows of the surface than it has samples, so the line at its bottom edge falls past that
    /// sample — every line above it lands inside — and there it blends in the rows the decoder padded the texture
    /// with, which were never written. Stopping the coordinate keeps that line on the picture and leaves the rest of it
    /// untouched: nothing that was already inside is changed.
    /// </summary>
    internal static (float X, float Y) GetTexCoordMax(int textureWidth, int textureHeight, int frameWidth, int frameHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return (1.0f, 1.0f);
        }

        return (
            frameWidth >= textureWidth ? 1.0f : (frameWidth - 0.5f) / textureWidth,
            frameHeight >= textureHeight ? 1.0f : (frameHeight - 0.5f) / textureHeight);
    }

    /// <summary>
    /// The same, for the chroma plane, which ends one texel earlier: it has half the rows and half the columns, so the
    /// coordinate of the picture's last luma sample is half a chroma sample past its last chroma sample. A picture drawn
    /// at its own size is enough for the bottom line of the surface to fall between those two, and the padding it blends
    /// in is zero — which is not a darker black but a saturated green, because chroma zero is the far end of both axes.
    /// </summary>
    internal static (float X, float Y) GetChromaTexCoordMax(int textureWidth, int textureHeight, int frameWidth, int frameHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return (1.0f, 1.0f);
        }

        return (
            frameWidth >= textureWidth ? 1.0f : (frameWidth - 1) / (float)textureWidth,
            frameHeight >= textureHeight ? 1.0f : (frameHeight - 1) / (float)textureHeight);
    }

    #endregion

    #region Software buffer

    private ID3D11Texture2D[] _planeTextures = [];
    private ID3D11ShaderResourceView[] _planeViews = [];

    private OperationResult Upload(RawVideoFrame frame, IPlanarVideoBuffer buffer)
    {
        (DXGI_FORMAT Format, int Width, int Height)[] layout = GetPlaneLayout(frame.Format, frame.Width, frame.Height);
        if (layout.Length == 0)
        {
            return OperationResult.Failure($"The Direct3D renderer cannot upload {frame.Format} frames.");
        }

        if (buffer.PlaneCount < layout.Length)
        {
            return OperationResult.Failure($"The frame has {buffer.PlaneCount} planes but {frame.Format} needs {layout.Length}.");
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
                return OperationResult.Failure($"Plane {plane} of the frame is not readable.");
            }

            // The box is expressed in texels, so the source row pitch stays whatever the decoder used.
            D3D11Interop.UploadTexture(_device.Context, _planeTextures[plane], layout[plane].Width, layout[plane].Height, pixels, stride);
        }

        return OperationResult.Success();
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
            PixelFormat.Bgra8 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, width, height),
            ],
            PixelFormat.Rgba8 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, width, height),
            ],
            PixelFormat.Nv12 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.P010 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.P016 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.Yuv420P =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, chromaHeight),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.Yuv420P10 =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, chromaWidth, chromaHeight),
                (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, chromaWidth, chromaHeight),
            ],
            PixelFormat.Yuv422P =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, chromaWidth, height),
            ],
            PixelFormat.Yuv444P =>
            [
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
                (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, width, height),
            ],
            _ => [],
        };
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

    private OperationResult Draw(ID3D11PixelShader pixelShader, ID3D11ShaderResourceView[] views, uint textureSlot, (float X, float Y) texCoords, (float X, float Y) texCoordMax, (float X, float Y) chromaTexCoordMax, VideoTransform transform)
    {
        ID3D11DeviceContext context = _device.Context;
        context.OMSetRenderTargets(1, [_outputView], null);

        // Anything the picture does not reach has to be painted, or what is left of the frame before it
        // shows through: the surface is a window, not the video.
        context.ClearRenderTargetView(_outputView, [0.0f, 0.0f, 0.0f, 1.0f]);

        // The whole surface is the viewport and the picture is placed inside it by the transform, which is what
        // lets fitting, turning and zooming be one thing: the picture is drawn where it belongs rather than into
        // a viewport shaped like it, and the bars are simply what the picture does not reach.
        D3D11Interop.SetViewport(context, 0, 0, _outputWidth, _outputHeight);
        D3D11Interop.SetRasterizerState(context, _shaders.Rasterizer);
        context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        D3D11Interop.SetShaders(context, _shaders.VertexShader, pixelShader);

        // Where the picture lies inside the texture it is drawn from: all of it for an uploaded frame, less
        // for one a hardware decoder padded. Where it lies on the surface is the matrix beside it. Beside the
        // span is the coordinate past which the picture has no samples of its own.
        Matrix3x2 placement = transform.GetPictureToSurfaceMatrix(_frameWidth, _frameHeight, _outputWidth, _outputHeight);
        D3D11Interop.SetFrameTransform(context, _frameTransform, texCoords, texCoordMax, chromaTexCoordMax, placement);
        D3D11Interop.SetFrameConstantBuffer(context, 0, _frameTransform);

        D3D11Interop.SetTextures(context, textureSlot, views, _shaders.GetSampler(transform.Scaling));

        // Four corners, placed by the transform: no vertex or index buffer is needed, and nothing outside the
        // picture is ever drawn.
        context.Draw(4, 0);

        _lastPixelShader = pixelShader;
        _lastViews = views;
        _lastSlot = textureSlot;
        _lastTexCoords = texCoords;
        _lastTexCoordMax = texCoordMax;
        _lastChromaTexCoordMax = chromaTexCoordMax;

        return _surface is null ? OperationResult.Success() : _surface.Present();
    }

    #endregion

    #region Surface

    private readonly VideoSurfacePreference _surfacePreference;
    private IVideoRenderTarget? _target;
    private IVideoSurface? _surface;
    private VideoSurfaceKind _surfaceKind = VideoSurfaceKind.Sdr;
    private (int Width, int Height, double Scale) _appliedSize;

    private OperationResult MatchSurfaceSize()
    {
        if (_surface is not { } surface || _target is not { } target)
        {
            return OperationResult.Success();
        }

        (int width, int height, double scale) = GetSurfaceSize(target);
        if (_appliedSize == (width, height, scale))
        {
            return OperationResult.Success();
        }

        OperationResult resize = surface.Resize(width, height, scale);
        if (!resize.IsSuccessful)
        {
            return resize;
        }

        _appliedSize = (width, height, scale);
        SetOutput(surface.View, surface.Width, surface.Height);
        return OperationResult.Success();
    }

    private static (int Width, int Height, double Scale) GetSurfaceSize(IVideoRenderTarget target)
    {
        double scale = target.RasterizationScale > 0 ? target.RasterizationScale : 1.0;
        if (!double.IsFinite(scale))
        {
            scale = 1.0;
        }

        return (
            Math.Max(1, (int)Math.Round(target.SurfaceWidth * scale)),
            Math.Max(1, (int)Math.Round(target.SurfaceHeight * scale)),
            scale);
    }

    #endregion

    #region Misc

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

        foreach (ID3D11ShaderResourceView view in _gpuViews)
        {
            D3D11Interop.Release(view);
        }

        _planeViews = [];
        _planeTextures = [];
        _gpuViews = [];
        _gpuTexture = 0;
        _gpuSlice = -1;

        // The views above held the texture; the frame held the slice. Both go together.
        _retainedFrame?.Dispose();
        _retainedFrame = null;

        _lastPixelShader = null;
        _lastViews = [];
        _frameFormat = PixelFormat.Unknown;
        _frameWidth = 0;
        _frameHeight = 0;
    }

    private string Describe(VideoSurfaceKind? kind)
    {
        string software = _device.IsSoftware ? ", software" : string.Empty;
        return kind switch
        {
            VideoSurfaceKind.Hdr10 => $"Direct3D 11 (HDR10{software})",
            VideoSurfaceKind.Sdr => $"Direct3D 11 (SDR{software})",
            _ => _device.IsSoftware ? "Direct3D 11 (software)" : "Direct3D 11",
        };
    }

    #endregion
}