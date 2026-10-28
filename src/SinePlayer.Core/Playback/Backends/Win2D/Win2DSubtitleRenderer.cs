// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Numerics;
using System.Runtime.InteropServices;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

using SinePlayer.Core.Common.ErrorHandling;
using SinePlayer.Core.Playback.Abstractions;
using SinePlayer.Core.Playback.Abstractions.Decode;
using SinePlayer.Core.Playback.Abstractions.Info;
using SinePlayer.Core.Playback.Abstractions.Render;
using SinePlayer.Core.Playback.Backends.Direct3D11;

using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SinePlayer.Core.Playback.Backends.Win2D;

public sealed partial class Win2DSubtitleRenderer : ISubtitleRenderer
{
    /// <summary>How tall a line is, as a fraction of the surface: big enough to read at any window size.</summary>
    private const float FontSizeFraction = 0.045f;

    private const float MinimumFontSize = 12.0f;

    private const float MaximumFontSize = 72.0f;

    /// <summary>How far the text is kept from the edge it is anchored to, as a fraction of the surface.</summary>
    private const float MarginFraction = 0.03f;

    /// <summary>How far the shadow sits from the text, in physical pixels: what makes light text readable on light frames.</summary>
    private const float ShadowOffset = 2.0f;

    private static readonly Guid sSurfaceInterfaceId = typeof(Windows.Win32.Graphics.Dxgi.IDXGISurface).GUID;

    /// <summary><c>IDXGIDevice</c>, which a Direct3D device answers to and Win2D wants.</summary>
    private static readonly Guid sDeviceInterfaceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private readonly string _fontFamily = "Segoe UI";

    private D3D11Device? _d3dDevice;
    private D3D11SwapChain? _swapChain;
    private CanvasDevice? _canvasDevice;
    private IVideoRenderTarget? _target;
    private SubtitleStreamInfo? _stream;
    private bool _disposed;

    public string Name => "Win2D";

    public OperationResult Configure(SubtitleStreamInfo stream, IVideoRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        if (!ReferenceEquals(_target, target) && _target is not null)
        {
            _target.SurfaceSizeChanged -= OnSurfaceSizeChanged;
            _target = null;
        }

        _stream = stream;
        _target = target;
        return EnsureSurface();
    }

    public OperationResult Show(IReadOnlyList<SubtitleCue> cues, SubtitlePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(cues);

        if (_disposed)
        {
            return OperationResult.Failure("The subtitle renderer has already been disposed.");
        }

        // The surface may have been resized since the last drawing, so the size is read every time rather than
        // remembered from Configure: a caller redraws after a resize precisely so this can take effect.
        OperationResult sized = EnsureSurface();
        if (!sized.IsSuccessful)
        {
            return sized;
        }

        D3D11SwapChain swapChain = _swapChain!;

        OperationResult drawn = Draw(swapChain, cues, placement);
        if (!drawn.IsSuccessful)
        {
            return drawn;
        }

        try
        {
            swapChain.Present();
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            // A lost device is not a reason to stop playback: the next drawing builds everything again.
            ResetSurface();
            return OperationResult.Failure($"Unable to present the subtitles: {exception.Message}");
        }
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
        }

        ResetSurface();
        _target = null;
        _stream = null;
    }

    private OperationResult Draw(D3D11SwapChain swapChain, IReadOnlyList<SubtitleCue> cues, SubtitlePlacement placement)
    {
        CanvasRenderTarget? target = null;
        try
        {
            int width = swapChain.Width;
            int height = swapChain.Height;

            // A fresh wrapper per drawing rather than one kept alive: a resize replaces the buffer it points at, and a
            // drawing is rare enough — a cue boundary or a resize — that holding one would buy nothing.
            IDirect3DSurface surface = CreateSurface(swapChain);
            target = CanvasRenderTarget.CreateFromDirect3D11Surface(_canvasDevice!, surface, 96.0f, CanvasAlphaMode.Premultiplied);

            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(default(Color));
                DrawCues(session, cues, placement, width, height);
            }

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

    /// <summary>
    /// Draws the visible cues onto a render target, which is the whole of what this renderer does with them — kept apart
    /// from the swap chain so the drawing can be exercised against an offscreen target, where the pixels can be read
    /// back. The caller clears the target; an empty set draws nothing onto it.
    /// </summary>
    internal void DrawCues(CanvasDrawingSession session, IReadOnlyList<SubtitleCue> cues, SubtitlePlacement placement, int width, int height)
    {
        if (cues.Count == 0 || width <= 0 || height <= 0)
        {
            // A cleared frame is a fully transparent one, which is what shows the picture and nothing else.
            return;
        }

        float fontSize = Math.Clamp(height * FontSizeFraction, MinimumFontSize, MaximumFontSize);
        float margin = height * MarginFraction;

        using CanvasTextFormat format = new()
        {
            FontFamily = _fontFamily,
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        foreach (SubtitleCue cue in cues)
        {
            if (cue.Image is { } image)
            {
                DrawImage(session, image, placement, width, height);
                continue;
            }

            var text = AssSubtitleText.Parse(cue.Text);
            if (text.IsEmpty)
            {
                continue;
            }

            string body = string.Join("\n", text.Lines);
            using CanvasTextLayout layout = new(_canvasDevice, body, format, width, height);

            Vector2 origin = Anchor(layout, text.Alignment, width, height, margin);
            origin += new Vector2((float)(placement.OffsetX * width), (float)(placement.OffsetY * height));
            origin = KeepOnSurface(origin, layout, width, height);

            session.DrawTextLayout(layout, origin + new Vector2(ShadowOffset, ShadowOffset), Color.FromArgb(200, 0, 0, 0));
            session.DrawTextLayout(layout, origin, Color.FromArgb(255, 255, 255, 255));
        }
    }

    /// <summary>
    /// Draws a bitmap cue where it belongs: its placement is a fraction of the frame the subtitle was authored against,
    /// so it is scaled to this surface and moved by the same adjustment text is moved by. The image arrives premultiplied,
    /// which is what a surface composed over the picture expects.
    /// </summary>
    private void DrawImage(CanvasDrawingSession session, SubtitleImage image, SubtitlePlacement placement, int width, int height)
    {
        ISubtitleImageBuffer buffer = image.Buffer;
        if (buffer.Width <= 0 || buffer.Height <= 0)
        {
            return;
        }

        byte[] pixels = new byte[buffer.Stride * buffer.Height];
        Marshal.Copy(buffer.Pixels, pixels, 0, pixels.Length);

        using var bitmap = CanvasBitmap.CreateFromBytes(
            _canvasDevice,
            pixels,
            buffer.Width,
            buffer.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96.0f,
            CanvasAlphaMode.Premultiplied);

        double x = ((image.Left + placement.OffsetX) * width);
        double y = ((image.Top + placement.OffsetY) * height);
        double scaledWidth = image.Width * width;
        double scaledHeight = image.Height * height;

        // Kept on the surface for the same reason the text is: an adjustment that pushes a cue off the edge is cut back
        // rather than obeyed, and a surface that later shrinks must not strand it.
        x = Math.Clamp(x, 0.0, Math.Max(0.0, width - scaledWidth));
        y = Math.Clamp(y, 0.0, Math.Max(0.0, height - scaledHeight));

        session.DrawImage(bitmap, new Rect(x, y, scaledWidth, scaledHeight));
    }

    /// <summary>
    /// Where a block of text goes before any adjustment: the ASS anchor, laid out against the surface with a margin from
    /// the edge it is anchored to. The layout is measured first, because centring needs its width.
    /// </summary>
    private static Vector2 Anchor(CanvasTextLayout layout, int alignment, int width, int height, float margin)
    {
        int index = Math.Clamp(alignment, 1, 9) - 1;
        int column = index % 3;
        int row = index / 3;

        float x = column switch
        {
            0 => margin,
            2 => width - (float)layout.LayoutBounds.Width - margin,
            _ => (width - (float)layout.LayoutBounds.Width) / 2.0f,
        };

        float y = row switch
        {
            0 => height - (float)layout.LayoutBounds.Height - margin,
            2 => margin,
            _ => (height - (float)layout.LayoutBounds.Height) / 2.0f,
        };

        return new Vector2(x, y);
    }

    /// <summary>
    /// Keeps the text on the surface. An adjustment that would push it off the edge is cut back, because the point of
    /// adjusting subtitles is still to read them — and a surface that later shrinks must not leave them stranded.
    /// </summary>
    private static Vector2 KeepOnSurface(Vector2 origin, CanvasTextLayout layout, int width, int height)
    {
        double maxX = Math.Max(0.0, width - layout.LayoutBounds.Width);
        double maxY = Math.Max(0.0, height - layout.LayoutBounds.Height);
        return new Vector2((float)Math.Clamp(origin.X, 0.0f, maxX), (float)Math.Clamp(origin.Y, 0.0f, maxY));
    }

    /// <summary>
    /// The device text is laid out with, built on first use. A test that wants to check the drawing against an offscreen
    /// target uses this rather than a swap chain, which cannot be read back after it has presented.
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

    private OperationResult EnsureSurface()
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
                // A device of its own: the picture's belongs to the video renderer, which owns and releases it.
                EnsureCanvasDevice();
            }

            if (_swapChain is null)
            {
                OperationResult<D3D11SwapChain> created = D3D11SwapChain.TryCreate(_d3dDevice!, width, height, scale, DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED);
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
                target.SurfaceSizeChanged += OnSurfaceSizeChanged;
                return OperationResult.Success();
            }

            OperationResult resized = _swapChain.Resize(width, height, scale);
            if (!resized.IsSuccessful)
            {
                ResetSurface();
                return resized;
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
    /// The Win2D device over the engine's Direct3D device, so drawing reaches the same swap chain rather than a device
    /// of Win2D's own choosing.
    /// </summary>
    private static IDirect3DDevice CreateWin2DDevice(D3D11Device device)
    {
        nint dxgiDevice = QueryInterface(device.Device, sDeviceInterfaceId);
        try
        {
            int created = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint graphicsDevice);
            if (created < 0)
            {
                throw Marshal.GetExceptionForHR(created) ?? new InvalidOperationException($"Unable to wrap the device for Win2D (0x{created:X8}).");
            }

            try
            {
                return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    /// <summary>The swap chain's back buffer as the surface type Win2D takes.</summary>
    private static IDirect3DSurface CreateSurface(D3D11SwapChain swapChain)
    {
        nint dxgiSurface = QueryInterface(swapChain.BackBuffer, sSurfaceInterfaceId);
        try
        {
            int created = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out nint graphicsSurface);
            if (created < 0)
            {
                throw Marshal.GetExceptionForHR(created) ?? new InvalidOperationException($"Unable to wrap the surface for Win2D (0x{created:X8}).");
            }

            try
            {
                return WinRT.MarshalInspectable<IDirect3DSurface>.FromAbi(graphicsSurface);
            }
            finally
            {
                Marshal.Release(graphicsSurface);
            }
        }
        finally
        {
            Marshal.Release(dxgiSurface);
        }
    }

    /// <summary>The same object as another interface, which is how a Direct3D object reaches the WinRT wrappers above.</summary>
    private static nint QueryInterface(object instance, Guid iid)
    {
        nint unknown = Marshal.GetIUnknownForObject(instance);
        try
        {
            int result = Marshal.QueryInterface(unknown, in iid, out nint acquired);
            if (result < 0)
            {
                throw Marshal.GetExceptionForHR(result) ?? new InvalidOperationException($"The object does not expose the interface (0x{result:X8}).");
            }

            return acquired;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    // The two exports that turn a Direct3D object into the WinRT surface and device Win2D draws through. Declared here
    // rather than generated, because both hand out a WinRT object through an ABI pointer and the generated wrappers
    // marshal an interface instead of a pointer.
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);

    private void OnSurfaceSizeChanged()
    {
        // A swap chain cannot be resized from the thread that raised this, and it does not need to be: the pipeline
        // draws again after telling the renderer, and EnsureSurface compares the size it is asked for with its own.
    }

    private void ResetSurface()
    {
        _swapChain?.Dispose();
        _swapChain = null;
        _canvasDevice?.Dispose();
        _canvasDevice = null;
        _d3dDevice?.Dispose();
        _d3dDevice = null;
    }
}
