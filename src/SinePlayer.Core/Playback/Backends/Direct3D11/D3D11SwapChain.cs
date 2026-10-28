// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Runtime.InteropServices;

using SinePlayer.Core.Common.ErrorHandling;
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
/// <para>
/// The buffers are sized in <em>physical</em> pixels and the composition scale of the host's element is
/// cancelled with <c>IDXGISwapChain2::SetMatrixTransform</c>. Both halves are needed: an element that
/// reports a scale applies it to the swap chain content when it composes, so a buffer of physical size
/// would be scaled a second time and a buffer of logical size would be enlarged by the display. The
/// transform is applied here rather than by the caller because <c>ResizeBuffers</c> resets it, which
/// makes "resized but no longer cancelling the scale" the easy mistake to make.
/// </para>
/// </summary>
internal sealed partial class D3D11SwapChain : IDisposable
{
    private const uint BufferCount = 2;

    private readonly D3D11Device _device;

    /// <summary>
    /// The format every buffer of this chain holds, and what a resize has to ask for again: <c>ResizeBuffers</c> takes
    /// a format and refuses to change it, so a chain created for one container cannot become another.
    /// </summary>
    private readonly DXGI_FORMAT _format;

    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _view;
    private bool _disposed;

    private D3D11SwapChain(D3D11Device device, DXGI_FORMAT format, IDXGISwapChain1 swapChain, ID3D11Texture2D backBuffer, ID3D11RenderTargetView view, int width, int height, double compositionScale)
    {
        _device = device;
        _format = format;
        _swapChain = swapChain;
        _backBuffer = backBuffer;
        _view = view;
        Width = width;
        Height = height;
        CompositionScale = compositionScale;
    }

    internal ID3D11RenderTargetView View => _view ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));

    /// <summary>
    /// The texture being presented, which a drawing that does not go through the render target view — Win2D drawing its
    /// own text, say — needs in order to reach the same surface.
    /// </summary>
    internal ID3D11Texture2D BackBuffer => _backBuffer ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));

    internal ID3D11Device Device => _device.Device;

    internal DXGI_FORMAT Format => _format;

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>The scale of the host's element, whose effect on the swap chain content is cancelled here.</summary>
    internal double CompositionScale { get; private set; }

    /// <param name="width">Buffer width in physical pixels, as the host's scale requires.</param>
    /// <param name="height">Buffer height in physical pixels, as the host's scale requires.</param>
    /// <param name="compositionScale">The scale the host's element will apply when it composes this swap chain.</param>
    internal static OperationResult<D3D11SwapChain> TryCreate(D3D11Device device, int width, int height, double compositionScale)
    {
        // A picture covers its whole surface, so what is behind it is never meant to show through.
        return TryCreate(device, width, height, compositionScale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    }

    /// <param name="alphaMode">
    /// Whether the element the swap chain is composed into can see through it. A surface meant to sit over another one —
    /// subtitles over a picture — has to be premultiplied, or its "nothing here" pixels arrive as opaque black and hide
    /// what is underneath.
    /// </param>
    internal static OperationResult<D3D11SwapChain> TryCreate(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode)
    {
        return TryCreate(device, width, height, compositionScale, alphaMode, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    }

    /// <param name="format">
    /// What each buffer holds. It is the half of a container DXGI is told at creation; the other half, the colour space,
    /// is declared afterwards with <see cref="TrySetColourSpace"/>.
    /// </param>
    internal static OperationResult<D3D11SwapChain> TryCreate(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode, DXGI_FORMAT format)
    {
        OperationResultCreator<D3D11SwapChain> result = new();

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        compositionScale = NormalizeScale(compositionScale);

        try
        {
            Guid factoryId = typeof(IDXGIFactory2).GUID;
            HRESULT factoryResult = PInvoke.CreateDXGIFactory2(default, factoryId, out object factoryObject);
            if (factoryResult.Value < 0 || factoryObject is not IDXGIFactory2 factory)
            {
                return result.Failure($"Unable to create a DXGI factory: {factoryResult}");
            }

            DXGI_SWAP_CHAIN_DESC1 description = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = alphaMode,
            };

            Graphics_Dxgi_IDXGIFactory2_Extensions.CreateSwapChainForComposition(factory, device.Device, description, null, out IDXGISwapChain1 created);
            if (created is null)
            {
                return result.Failure("Unable to create a composition swap chain.");
            }

            OperationResult scaled = TrySetCompositionScale(created, compositionScale);
            if (!scaled.IsSuccessful)
            {
                D3D11Interop.Release(created);
                return result.Failure(scaled);
            }

            ID3D11Texture2D backBuffer = GetBackBuffer(created);
            ID3D11RenderTargetView view = D3D11Interop.CreateRenderTargetView(device.Device, backBuffer);

            return result.Success(new D3D11SwapChain(device, format, created, backBuffer, view, width, height, compositionScale));
        }
        catch (Exception exception)
        {
            return result.Failure($"Unable to create a composition swap chain: {exception.Message}");
        }
    }

    /// <summary>Hands the swap chain to the host, which binds it to its own presentation element.</summary>
    internal OperationResult AttachTo(IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed || _swapChain is null)
        {
            return OperationResult.Failure("The swap chain has already been disposed.");
        }

        nint handle = Marshal.GetIUnknownForObject(_swapChain);
        try
        {
            target.AttachPresentationSurface(handle);
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to attach the presentation surface: {exception.Message}");
        }
        finally
        {
            // The target takes its own reference; ours was only for the hand-off.
            Marshal.Release(handle);
        }

        return OperationResult.Success();
    }

    /// <summary>Reads the current back buffer into system memory, for capture and verification.</summary>
    internal byte[] ReadPixels()
    {
        ID3D11Texture2D backBuffer = _backBuffer ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));
        return D3D11Interop.ReadPixels(_device.Device, _device.Context, backBuffer, Width, Height, _format);
    }
    /// <summary>
    /// Declares what the buffer's codes mean, which is the half of a container DXGI cannot be told at creation.
    /// <para>
    /// A swap chain created without this says eight-bit BT.709 gamma relative to the display's white, so writing PQ
    /// codes into one is not an error DXGI reports — it is a picture that is wrong in a way nobody is told about. The
    /// support check is therefore part of the operation rather than an assumption, and a refusal is returned so the
    /// caller can fall back instead of presenting a surface whose meaning it could not establish.
    /// </para>
    /// </summary>
    internal unsafe OperationResult TrySetColourSpace(DXGI_COLOR_SPACE_TYPE colourSpace)
    {
        if (_disposed || _swapChain is null)
        {
            return OperationResult.Failure("The swap chain has already been disposed.");
        }

        if (_swapChain is not IDXGISwapChain3 swapChain3)
        {
            return OperationResult.Failure("The swap chain does not offer a colour space, so it cannot carry absolute light levels.");
        }

        try
        {
            // Generated as a throwing wrapper, so a chain that cannot answer arrives as an exception below rather than
            // as a value to check.
            swapChain3.CheckColorSpaceSupport(colourSpace, out DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG support);

            if ((support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG.DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) == 0)
            {
                return OperationResult.Failure($"The swap chain does not accept {colourSpace} in its {_format} buffers.");
            }

            swapChain3.SetColorSpace1(colourSpace);
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to declare {colourSpace} on the swap chain: {exception.Message}");
        }
    }

    /// <summary>
    /// Sizes the buffers and re-applies the composition scale transform, which <c>ResizeBuffers</c>
    /// throws away.
    /// </summary>
    internal OperationResult Resize(int width, int height, double compositionScale)
    {
        // Taken once: a chain disposed from another thread is the only way this can become null, and using the same
        // reference throughout is what keeps the resize from reaching a chain that was released mid-call.
        IDXGISwapChain1? swapChain = _swapChain;
        if (_disposed || swapChain is null)
        {
            return OperationResult.Failure("The swap chain has already been disposed.");
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        compositionScale = NormalizeScale(compositionScale);

        if (width == Width && height == Height && compositionScale == CompositionScale)
        {
            return OperationResult.Success();
        }

        try
        {
            // Buffers cannot be resized while they are still bound.
            _device.Context.OMSetRenderTargets(0, null, null);
            ReleaseBackBuffer();

            // The format is the one the chain was created with: a resize cannot change it, and passing another would
            // fail rather than quietly repaint the pictures with another container's meaning.
            swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, _format, default);

            OperationResult scaled = TrySetCompositionScale(swapChain, compositionScale);
            if (!scaled.IsSuccessful)
            {
                return scaled;
            }

            ID3D11Texture2D backBuffer = GetBackBuffer(swapChain);
            _backBuffer = backBuffer;
            _view = D3D11Interop.CreateRenderTargetView(_device.Device, backBuffer);
            Width = width;
            Height = height;
            CompositionScale = compositionScale;
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to resize the swap chain: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads back the transform DXGI holds, which is what the host's element will apply — the test uses
    /// it to check that the scale is actually cancelled rather than merely intended.
    /// </summary>
    internal unsafe bool TryGetCompositionTransform(out double scaleX, out double scaleY)
    {
        scaleX = 1.0;
        scaleY = 1.0;

        if (_disposed || _swapChain is not IDXGISwapChain2 swapChain2)
        {
            return false;
        }

        try
        {
            DXGI_MATRIX_3X2_F matrix;
            swapChain2.GetMatrixTransform(&matrix);
            scaleX = matrix._11;
            scaleY = matrix._22;
            return true;
        }
        catch (Exception)
        {
            // A swap chain that cannot report its transform cannot be checked either; the caller treats
            // that the same way as a missing transform.
            return false;
        }
    }

    /// <summary>
    /// Cancels the scale the host's element applies when it composes the swap chain, so a buffer of
    /// physical pixels lands on the display one pixel to one pixel.
    /// </summary>
    private static unsafe OperationResult TrySetCompositionScale(IDXGISwapChain1 swapChain, double compositionScale)
    {
        if (swapChain is not IDXGISwapChain2 swapChain2)
        {
            return OperationResult.Failure("The swap chain does not support a transform, so it cannot be shown at device resolution.");
        }

        float inverse = (float)(1.0 / compositionScale);
        DXGI_MATRIX_3X2_F matrix = new()
        {
            _11 = inverse,
            _22 = inverse,
        };

        try
        {
            swapChain2.SetMatrixTransform(&matrix);
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Unable to cancel the display scale on the swap chain: {exception.Message}");
        }

        return OperationResult.Success();
    }

    private static double NormalizeScale(double scale)
    {
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// Hands the frame to the compositor and returns without waiting for a vertical blank.
    /// <para>
    /// Waiting for the blank — presenting with a sync interval of one — makes the display a second clock that
    /// the pipeline cannot see. The wait lands inside the presentation thread's frame, is counted as the
    /// renderer's cost, and at a frame rate equal to the display's it leaves the thread no slack at all: the
    /// lateness of each frame ratchets up by whatever the loop costs until the drop rule fires, and the drops
    /// are steady from then on. Measured on a 1080p60 file whose renderer does the whole frame in 0.38 ms:
    /// <c>render 18.0 ms</c> and about a tenth of the frames dropped, with a lag of 70 ms — which is the drop
    /// rule's tolerance plus one display interval. It was 17.6 ms of waiting, not work.
    /// </para>
    /// <para>
    /// The clock is what paces the picture (rule 4); the display only decides what it shows, and a composition
    /// swap chain reads the newest buffer at its own composition. Presenting without the wait still blocks
    /// when every buffer is in flight, which is the compositor being behind and cannot be helped — but that is
    /// the exception, where waiting for the blank was the rule. The cost of that choice is that a frame can be
    /// presented and then superseded before the display takes it; it is counted as presented like any other.
    /// </para>
    /// </summary>
    internal OperationResult Present()
    {
        if (_disposed || _swapChain is null)
        {
            return OperationResult.Failure("The swap chain has already been disposed.");
        }

        HRESULT result = _swapChain.Present(0, default);
        if (result.Value < 0)
        {
            return OperationResult.Failure($"Unable to present a frame: {result}");
        }

        return OperationResult.Success();
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