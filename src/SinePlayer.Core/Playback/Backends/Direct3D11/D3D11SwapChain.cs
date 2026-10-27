// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Diagnostics.CodeAnalysis;
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
internal sealed class D3D11SwapChain : IDisposable
{
    private const uint BufferCount = 2;

    private readonly D3D11Device _device;

    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _view;
    private bool _disposed;

    /// <summary>The monitor's count when this swap chain first asked, or -1 while nothing has answered yet.</summary>
    private long _displayedBaseline = -1;

    /// <summary>What the monitor's count was the last time it answered, or -1 if it never has.</summary>
    private long _displayedCount = -1;

    private D3D11SwapChain(D3D11Device device, IDXGISwapChain1 swapChain, ID3D11Texture2D backBuffer, ID3D11RenderTargetView view, int width, int height, double compositionScale)
    {
        _device = device;
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

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>The scale of the host's element, whose effect on the swap chain content is cancelled here.</summary>
    internal double CompositionScale { get; private set; }

    /// <param name="width">Buffer width in physical pixels, as the host's scale requires.</param>
    /// <param name="height">Buffer height in physical pixels, as the host's scale requires.</param>
    /// <param name="compositionScale">The scale the host's element will apply when it composes this swap chain.</param>
    internal static bool TryCreate(D3D11Device device, int width, int height, double compositionScale, [NotNullWhen(true)] out D3D11SwapChain? swapChain, out string error)
    {
        // A picture covers its whole surface, so what is behind it is never meant to show through.
        return TryCreate(device, width, height, compositionScale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE, out swapChain, out error);
    }

    /// <param name="alphaMode">
    /// Whether the element the swap chain is composed into can see through it. A surface meant to sit over another one —
    /// subtitles over a picture — has to be premultiplied, or its "nothing here" pixels arrive as opaque black and hide
    /// what is underneath.
    /// </param>
    internal static bool TryCreate(D3D11Device device, int width, int height, double compositionScale, DXGI_ALPHA_MODE alphaMode, [NotNullWhen(true)] out D3D11SwapChain? swapChain, out string error)
    {
        swapChain = null;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        compositionScale = NormalizeScale(compositionScale);

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
                AlphaMode = alphaMode,
            };

            Graphics_Dxgi_IDXGIFactory2_Extensions.CreateSwapChainForComposition(factory, device.Device, description, null, out IDXGISwapChain1 created);
            if (created is null)
            {
                error = "Unable to create a composition swap chain.";
                return false;
            }

            if (!TrySetCompositionScale(created, compositionScale, out error))
            {
                D3D11Interop.Release(created);
                return false;
            }

            ID3D11Texture2D backBuffer = GetBackBuffer(created);
            ID3D11RenderTargetView view = D3D11Interop.CreateRenderTargetView(device.Device, backBuffer);

            swapChain = new D3D11SwapChain(device, created, backBuffer, view, width, height, compositionScale);
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
        return D3D11Interop.ReadPixels(_device.Device, _device.Context, backBuffer, Width, Height);
    }
    /// <summary>
    /// Sizes the buffers and re-applies the composition scale transform, which <c>ResizeBuffers</c>
    /// throws away.
    /// </summary>
    internal OperationResult Resize(int width, int height, double compositionScale)
    {
        if (_disposed)
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

            _swapChain!.ResizeBuffers(BufferCount, (uint)width, (uint)height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, default);

            if (!TrySetCompositionScale(_swapChain, compositionScale, out string error))
            {
                return OperationResult.Failure(error);
            }

            _backBuffer = GetBackBuffer(_swapChain);
            _view = D3D11Interop.CreateRenderTargetView(_device.Device, _backBuffer);
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
    private static unsafe bool TrySetCompositionScale(IDXGISwapChain1 swapChain, double compositionScale, out string error)
    {
        error = string.Empty;

        if (swapChain is not IDXGISwapChain2 swapChain2)
        {
            error = "The swap chain does not support a transform, so it cannot be shown at device resolution.";
            return false;
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
            error = $"Unable to cancel the display scale on the swap chain: {exception.Message}";
            return false;
        }

        return true;
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
    /// the exception, where waiting for the blank was the rule. What the cost of that choice is — a frame the
    /// compositor supersedes is presented and never shown — is what <see cref="TryGetDisplayedFrameCount"/>
    /// exists to make visible.
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

        // The count of hand-overs is the pipeline's (it is the same number and it survives a renderer swap);
        // what is kept here is only what the system said about them.
        SampleDisplayedFrameCount();
        return OperationResult.Success();
    }

    /// <summary>
    /// How many of the frames handed over have reached the monitor, according to the system, or false when it
    /// will not say.
    /// <para>
    /// <c>PresentCount</c> is "the running total count of times that an image was presented to the monitor",
    /// which the documentation is careful to distinguish from the number of <c>Present</c> calls: the
    /// difference between the two is what the compositor superseded. In windowed mode the structure's
    /// documentation says these values are based on when the swap chain was created (full-screen ones are
    /// based on boot), so the count belongs to this chain and starts near zero — the first answer is taken as
    /// the baseline rather than assuming zero.
    /// </para>
    /// <para>
    /// It is not always available: the system declines on some drivers, the statistics are documented as
    /// unreliable with several monitors or another fullscreen application, and on systems with a hardware flip
    /// queue they may need <c>DwmFlush</c> to be current. A number that stops advancing is one of those, not
    /// a stall — which is why the caller is told whether there is a number at all rather than being handed
    /// the last one as if it were now.
    /// </para>
    /// <para>
    /// Read after a present, on the one thread that presents, because a swap chain is not safe to touch from
    /// anywhere else.
    /// </para>
    /// </summary>
    internal bool TryGetDisplayedFrameCount(out long count)
    {
        count = Interlocked.Read(ref _displayedCount);
        return count >= 0;
    }

    private void SampleDisplayedFrameCount()
    {
        if (_disposed || _swapChain is null)
        {
            return;
        }

        DXGI_FRAME_STATISTICS statistics;
        try
        {
            // Generated as a throwing wrapper — the one DXGI method the metadata does not mark PreserveSig —
            // so a driver that declines arrives here as an exception rather than as a value to check, which
            // is the same shrug with fewer branches.
            _swapChain.GetFrameStatistics(out statistics);
        }
        catch (Exception)
        {
            // A driver that will not answer keeps the last answer it gave, or none if it never gave one.
            return;
        }

        long baseline = Interlocked.Read(ref _displayedBaseline);
        if (baseline < 0)
        {
            baseline = statistics.PresentCount;
            Interlocked.Exchange(ref _displayedBaseline, baseline);
        }

        // Subtracted in unsigned arithmetic so a wrap of the system's 32 bit counter reads as the small
        // number it is rather than as a jump of four billion.
        uint displayed = unchecked((uint)(statistics.PresentCount - (uint)baseline));
        Interlocked.Exchange(ref _displayedCount, displayed);
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