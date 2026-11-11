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

internal sealed partial class D3D11SwapChain : IDisposable
{
    private const uint BufferCount = 2;
    private const uint FrameSlotTimeoutMilliseconds = 50;

    /// <param name="width">Buffer width in physical pixels, as the host's scale requires.</param>
    /// <param name="height">Buffer height in physical pixels, as the host's scale requires.</param>
    /// <param name="compositionScale">The scale the host's element will apply when it composes this swap chain.</param>
    internal static OperationResult<D3D11SwapChain> Create(D3D11Device device, int width, int height, double compositionScale)
    {
        // A picture covers its whole surface, so what is behind it is never meant to show through.
        return Create(device, width, height, compositionScale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    }

    /// <param name="alphaMode">
    /// Whether the element the swap chain is composed into can see through it. A surface meant to sit over another one —
    /// subtitles over a picture — has to be premultiplied, or its "nothing here" pixels arrive as opaque black and hide
    /// what is underneath.
    /// </param>
    internal static OperationResult<D3D11SwapChain> Create(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode)
    {
        return Create(device, width, height, compositionScale, alphaMode, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    }

    /// <param name="format">
    /// What each buffer holds. It is the half of a container DXGI is told at creation; the other half, the colour space,
    /// is declared afterwards with <see cref="TrySetColourSpace"/>.
    /// </param>
    internal static OperationResult<D3D11SwapChain> Create(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode, DXGI_FORMAT format)
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
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
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

            D3D11SwapChain chain = new(device, format)
            {
                _swapChain = created,
                _backBuffer = backBuffer,
                _view = view,
                _frameLatency = TryEnableFrameLatencyPacing(created),
                Width = width,
                Height = height,
                CompositionScale = compositionScale
            };

            return result.Success(chain);
        }
        catch (Exception exception)
        {
            return result.Failure($"Unable to create a composition swap chain: {exception.Message}");
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
    /// Hands back the object that says when the display has taken the frame it was holding, for a chain created with
    /// <c>DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT</c>. One frame in flight is what makes that answer mean
    /// "the display has taken the last frame"; a deeper queue would answer "there is room in the queue", which is a
    /// frame that arrives late or not at all.
    /// </summary>
    private static HANDLE TryEnableFrameLatencyPacing(IDXGISwapChain1 swapChain)
    {
        if (swapChain is not IDXGISwapChain2 swapChain2)
        {
            return default;
        }

        HANDLE handle;
        try
        {
            handle = swapChain2.GetFrameLatencyWaitableObject();
        }
        catch (Exception)
        {
            // A chain without the object still presents; it just cannot be told when it may, and nothing else about
            // the picture depends on it.
            return default;
        }

        try
        {
            swapChain2.SetMaximumFrameLatency(1);
        }
        catch (Exception)
        {
            // The flag already asks for one frame in flight, so a chain that will not be told so keeps that and loses
            // nothing; the handle is what this needed, and it is already in hand.
        }

        return handle;
    }

    private static unsafe ID3D11Texture2D GetBackBuffer(IDXGISwapChain1 swapChain)
    {
        Guid textureId = typeof(ID3D11Texture2D).GUID;
        swapChain.GetBuffer(0, &textureId, out object surface);
        return (ID3D11Texture2D)surface;
    }

    private readonly D3D11Device _device;

    private bool _disposed;
    private IDXGISwapChain1? _swapChain;
    private HANDLE _frameLatency;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _view;
    private readonly DXGI_FORMAT _format;

    private D3D11SwapChain(D3D11Device device, DXGI_FORMAT format)
    {
        _device = device;
        _format = format;
    }

    internal ID3D11RenderTargetView View => _view ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));

    internal ID3D11Texture2D BackBuffer => _backBuffer ?? throw new ObjectDisposedException(nameof(D3D11SwapChain));

    internal ID3D11Device Device => _device.Device;

    internal DXGI_FORMAT Format => _format;

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>The scale of the host's element, whose effect on the swap chain content is cancelled here.</summary>
    internal double CompositionScale { get; private set; }

    /// <summary>Whether the chain offers a signal for "the display has taken the last frame".</summary>
    internal unsafe bool HasFrameLatencyPacing => _frameLatency.Value != null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseBackBuffer();

        if (HasFrameLatencyPacing)
        {
            PInvoke.CloseHandle(_frameLatency);
            _frameLatency = default;
        }

        D3D11Interop.Release(_swapChain);
        _swapChain = null;
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
    internal OperationResult TrySetColourSpace(DXGI_COLOR_SPACE_TYPE colourSpace)
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
            // fail rather than quietly repaint the pictures with another container's meaning. The flags are passed on
            // for the same reason: the waitable object that paces the chain is part of them, and a resize that dropped
            // it would leave a chain that could no longer say when the display has taken a frame.
            swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, _format, DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);

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
    /// Waits for the display to take the frame it is holding, and reports whether it did so within the bound.
    /// <para>
    /// This is the pattern the frame latency object exists for, and the measurement that matters is what makes it work:
    /// after a present, the object stays unsignalled for a whole refresh (16.4 ms of a 60 Hz display, measured), and
    /// <see cref="Present"/> itself returns in a tenth of a millisecond because the flag moved the waiting out of it.
    /// The same object handed back by a chain presenting with a sync interval of zero says the opposite — it is
    /// signalled again immediately, because a chain presenting immediately keeps no frame outstanding to wait for —
    /// which is why <see cref="Present"/> asks for interval one.
    /// </para>
    /// <para>
    /// Waiting here rather than drawing and dropping is what keeps an exact-rate file from losing frames to its own
    /// jitter: at sixty frames into sixty refreshes the frame is due at the very moment the previous one is displayed,
    /// so a poll would lose whichever frames arrived early, and the loss would repeat. A file that outruns the display
    /// instead spends this time idle and is then dropped by the clock's own rule, which has the tolerance to tell late
    /// from early.
    /// </para>
    /// </summary>
    internal bool TryAcquireFrameSlot()
    {
        if (_disposed || _swapChain is null)
        {
            return false;
        }

        if (!HasFrameLatencyPacing)
        {
            return true;
        }

        // One refresh at the slowest refresh rate worth respecting, and then some: a display that has not taken a frame
        // in this long is not going to, and the caller drops it rather than drawing into a queue that never drains.
        return PInvoke.WaitForSingleObject(_frameLatency, FrameSlotTimeoutMilliseconds) == WAIT_EVENT.WAIT_OBJECT_0;
    }

    /// <summary>
    /// Hands the frame to the display and returns without waiting for a vertical blank, even though the interval asked
    /// for is one.
    /// <para>
    /// The interval is one because that is what the frame latency object needs: a chain presenting with zero keeps no
    /// frame outstanding, so the object is signalled again the moment the call returns and says nothing about the
    /// display. Measured on the same chain, both ways: presenting with interval zero, the wait that follows returns in
    /// 0.0 ms and the pacing is inert; presenting with interval one, it returns after 16.4 ms on a 60 Hz display, and
    /// this call itself still takes a tenth of a millisecond.
    /// </para>
    /// <para>
    /// That is the difference the flag makes. Waiting for the blank used to land inside this call, where it was the
    /// render thread's cost and could not be told apart from work: 17.6 ms of a 1080p60 frame's 18.0 ms, which left no
    /// slack at an equal frame rate and dropped about a tenth of the frames for good. With the flag the same waiting is
    /// the object's business, taken in <see cref="TryAcquireFrameSlot"/> before any work is done, so a frame that
    /// cannot reach the display is given up instead of drawn.
    /// </para>
    /// </summary>
    internal OperationResult Present()
    {
        if (_disposed || _swapChain is null)
        {
            return OperationResult.Failure("The swap chain has already been disposed.");
        }

        HRESULT result = _swapChain.Present(1, default);
        if (result.Value < 0)
        {
            return OperationResult.Failure($"Unable to present a frame: {result}");
        }

        return OperationResult.Success();
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

    private void ReleaseBackBuffer()
    {
        D3D11Interop.Release(_view);
        _view = null;
        D3D11Interop.Release(_backBuffer);
        _backBuffer = null;
    }
}